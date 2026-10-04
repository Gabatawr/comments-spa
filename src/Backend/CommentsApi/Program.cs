using CommentsApi.Data;
using CommentsApi.Dtos;
using CommentsApi.Endpoints;
using CommentsApi.Infrastructure.Cache;
using CommentsApi.Infrastructure.Events;
using CommentsApi.Infrastructure.Queue;
using CommentsApi.Infrastructure.WebSockets;
using CommentsApi.Services;
using CommentsApi.Validation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- services
builder.Services.Configure<AttachmentStorageOptions>(builder.Configuration.GetSection("Storage"));

// Resolve the connection string lazily from DI: tests (WebApplicationFactory) and Docker env
// overrides add configuration sources after this line, and they must win over the default.
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    options.UseSqlite(configuration.GetConnectionString("Default") ?? "Data Source=comments.db");
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<HtmlSanitizer>();
builder.Services.AddSingleton<ICaptchaService, CaptchaService>();
builder.Services.AddScoped<ICommentQueryService, CommentQueryService>();
builder.Services.AddScoped<ICommentCreateService, CommentCreateService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();

// Junior+: events -> queue -> cache / WebSocket, all in-process.
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddSingleton<CommentCreatedQueue>();
builder.Services.AddSingleton<CommentCache>();
builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddHostedService<CommentCreatedConsumer>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// ---------------------------------------------------------------- db bootstrap
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Force singleton event subscribers to attach to the bus.
_ = app.Services.GetRequiredService<CommentCache>();
_ = app.Services.GetRequiredService<WebSocketHub>();

// ---------------------------------------------------------------- pipeline
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    // Minimal-API binding failures (e.g. non-numeric /api/comments?page=...) surface as
    // BadHttpRequestException; preserve their 4xx status instead of masking it as a 500.
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = error is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError;

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

app.UseWebSockets();
app.Map("/ws", app.Services.GetRequiredService<WebSocketHub>().HandleAsync);

app.MapCommentsApi();

// Dev-only CAPTCHA peek (docs/API.md §2.10): requires Development AND Features:DevCaptchaPeek=true.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Features:DevCaptchaPeek"))
{
    app.MapGet("/api/dev/captcha/{captchaId}", (string captchaId, ICaptchaService captchaService) =>
    {
        var code = captchaService.PeekAnswer(captchaId);
        return code is null
            ? Results.Json(
                new ErrorResponse
                {
                    Title = "Not Found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = "Unknown or expired captchaId.",
                },
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new CaptchaPeekDto { CaptchaId = captchaId, Code = code });
    });
}

// ---------------------------------------------------------------- SPA hosting
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapFallback(async context =>
{
    var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();

    if (HttpMethods.IsGet(context.Request.Method))
    {
        var path = context.Request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
        if (!isApi && !Path.HasExtension(path))
        {
            var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var indexPath = Path.Combine(webRoot, "index.html");
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
