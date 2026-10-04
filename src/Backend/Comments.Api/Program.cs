using System.Net;
using Comments.Api.Endpoints;
using Comments.Api.GraphQL;
using Comments.Api.Infrastructure;
using Comments.Api.Infrastructure.WebSockets;
using Comments.Application;
using Comments.Application.Dtos;
using Comments.Infrastructure;
using Comments.Infrastructure.Persistence;
using Comments.Infrastructure.Search;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;

// ---------------------------------------------------------------------------
// Comments.Api composition root (docs/ARCHITECTURE-v2.md §4, docs/API-v2.md §6, §10).
//   Domain <- Application <- Infrastructure <- Api
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------- forwarded headers (§6)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

    if (builder.Configuration.GetValue<bool>("Proxy:TrustAll"))
    {
        // Inside the isolated compose network nginx is the only ingress: trust every proxy.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
    else
    {
        var knownProxies = builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>()
                           ?? Array.Empty<string>();
        foreach (var proxy in knownProxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }
    }
});

// ---------------------------------------------------------- application + infrastructure
builder.Services.AddCommentsApplication();
builder.Services.AddCommentsInfrastructure(builder.Configuration);

// API-layer services.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CommentCreationFacade>();
builder.Services.AddSingleton<WebSocketHub>();

// ---------------------------------------------------------- API docs
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// ---------------------------------------------------------- GraphQL (§5)
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddType<CommentType>()
    .AddType<AttachmentType>()
    .AddType<CommentPageType>()
    .AddType<SearchHitType>()
    .AddType<SearchPageType>()
    .AddType<ValidationErrorType>()
    .AddType<CreateCommentInputType>()
    .AddType<CreateCommentPayloadType>()
    .AddMaxExecutionDepthRule(15)
    .AddCostAnalyzer()
    .ModifyCostOptions(options => options.MaxFieldCost = 300);

var app = builder.Build();

// ---------------------------------------------------------- db / search bootstrap (idempotent)
await app.Services.ApplyMigrationsAsync();
await app.Services.EnsureSearchIndexAsync();

app.UseForwardedHeaders();

// ---------------------------------------------------------- pipeline
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    // Minimal-API binding failures (e.g. non-numeric /api/comments?page=...) surface as
    // BadHttpRequestException; preserve their 4xx status instead of masking them as a 500.
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = error is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError;

    var logger = context.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Comments.Api.ExceptionHandler");

    if (status >= 500)
    {
        // Full stack trace in `docker compose logs api` for every 500.
        logger.LogError(
            error,
            "Unhandled exception for {Method} {Path} -> {Status}",
            context.Request.Method,
            context.Request.Path,
            status);
    }
    else
    {
        logger.LogDebug(
            error,
            "Request rejected for {Method} {Path} -> {Status}",
            context.Request.Method,
            context.Request.Path,
            status);
    }

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ErrorResponse
    {
        Title = status >= 500 ? "Internal Server Error" : "Bad Request",
        Status = status,
        Detail = status >= 500
            ? "An unexpected error occurred."
            : "One or more request parameters are invalid.",
    });
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors();
}
else
{
    // Production exposes GraphQL over POST only (docs/API-v2.md §3.2); the GET endpoint serves
    // the HotChocolate IDE and must not be reachable outside Development.
    app.Use(async (context, next) =>
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await next();
    });
}

app.UseWebSockets();
app.Map("/ws", app.Services.GetRequiredService<WebSocketHub>().HandleAsync);

app.MapGraphQL("/graphql");
app.MapCommentsApi();

// ---------------------------------------------------------- SPA hosting (harmless when absent)
// In production nginx serves the Angular SPA and proxies /api + /ws here; this fallback only
// kicks in when static files actually exist (local single-process runs).
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapFallback(async context =>
{
    var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();

    if (HttpMethods.IsGet(context.Request.Method))
    {
        var path = context.Request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
        if (!isApi && !System.IO.Path.HasExtension(path))
        {
            var webRoot = environment.WebRootPath ?? System.IO.Path.Combine(environment.ContentRootPath, "wwwroot");
            var indexPath = System.IO.Path.Combine(webRoot, "index.html");
            if (File.Exists(indexPath))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexPath);
                return;
            }
        }
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsJsonAsync(new ErrorResponse
    {
        Title = "Not Found",
        Status = StatusCodes.Status404NotFound,
        Detail = $"No resource at {context.Request.Path}.",
    });
});

app.Run();

public partial class Program;
