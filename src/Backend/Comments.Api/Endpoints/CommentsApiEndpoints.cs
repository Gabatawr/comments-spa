using Comments.Api.Infrastructure;
using Comments.Api.Infrastructure.WebSockets;
using Comments.Application.Abstractions;
using Comments.Application.Abstractions.Caching;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Abstractions.Persistence;
using Comments.Application.Abstractions.Providers;
using Comments.Application.Abstractions.Search;
using Comments.Application.Abstractions.Storage;
using Comments.Application.Dtos;
using Comments.Application.Services;
using Comments.Application.Validation;
using Comments.Domain;
using Microsoft.Extensions.Primitives;

namespace Comments.Api.Endpoints;

/// <summary>
/// All REST endpoints: the v1 contract (docs/API.md §2, parity preserved) plus the v2 additions
/// (docs/API-v2.md §2–§3). Implemented as minimal APIs so the frozen 400 shape is exact.
/// </summary>
public static class CommentsApiEndpoints
{
    private const string CommentCreatedEventType = "CommentCreated";

    public static void MapCommentsApi(this WebApplication app)
    {
        // --- v1 endpoints (docs/API.md) ---------------------------------------------------
        app.MapGet("/api/health", HealthAsync);
        app.MapGet("/api/captcha", Captcha);
        app.MapGet("/api/comments", ListAsync);
        app.MapGet("/api/comments/{id:int}", GetByIdAsync);
        app.MapPost("/api/comments", CreateAsync);
        app.MapPost("/api/preview", Preview);
        app.MapGet("/api/attachments/{id:int}", DownloadAsync);
        app.MapGet("/api/attachments/{id:int}/thumb", ThumbAsync);

        // --- v2 endpoints (docs/API-v2.md §2.1, §3) ---------------------------------------
        app.MapGet("/api/health/live", Live);
        app.MapGet("/api/health/ready", ReadyAsync);
        app.MapGet("/api/info", InfoAsync);
        app.MapGet("/api/search", SearchAsync);
        app.MapGet("/api/stats", StatsAsync);
        app.MapPost("/api/comments/{id:int}/child", CreateChildAsync);

        // Dev-only CAPTCHA peek (docs/API-v2.md §2.8): Development AND Features:DevCaptchaPeek=true.
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Features:DevCaptchaPeek"))
        {
            app.MapGet("/api/dev/captcha/{captchaId}", CaptchaPeek);
        }

        // Seed endpoint (docs/API-v2.md §3.4): Development AND Features:Seed=true, else 404.
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Features:Seed"))
        {
            app.MapPost("/api/dev/seed", SeedAsync);
        }
    }

    // ------------------------------------------------------------------ health (§2.1)

    private static async Task<IResult> HealthAsync(
        IDatabaseHealthCheck databaseHealth,
        ICacheService cache,
        IEventConsumer consumer,
        ICommentSearchIndex searchIndex,
        IFileStorage storage,
        WebSocketHub hub,
        ActiveProviders providers,
        CancellationToken cancellationToken)
    {
        var database = "ok";
        try
        {
            if (!await databaseHealth.CanConnectAsync(cancellationToken))
            {
                database = "error";
            }
        }
        catch
        {
            database = "error";
        }

        // Every status comes from the provider's own probe plus the switchboard — no provider name
        // is spelled out here, so a new adapter shows up in health without touching this method.
        var cacheStatus = Status(cache.IsAvailable);

        return Results.Ok(new HealthDto
        {
            Status = database == "ok" ? "ok" : "degraded",
            Database = database,
            Cache = cacheStatus,
            Queue = new QueueHealthDto { Pending = consumer.Pending, Processed = consumer.Processed },
            Websocket = new WebSocketHealthDto { Clients = hub.ClientCount },
            Redis = providers.IsSelfContained(ProviderCatalog.Cache) ? "disabled" : cacheStatus,
            Broker = providers.IsSelfContained(ProviderCatalog.Messaging) ? "disabled" : Status(consumer.IsAvailable),
            Search = providers.IsSelfContained(ProviderCatalog.Search) ? "disabled" : Status(searchIndex.IsAvailable),
            Storage = Status(storage.IsAvailable),
            Providers = new Dictionary<string, string>(providers.Values, StringComparer.Ordinal),
            Version = ApiVersion(),
        });
    }

    /// <summary>
    /// GET /api/info — the provider switchboard as resolved at startup plus live reachability.
    /// Answers "what is this deployment running on, and what else could it run on" without
    /// exec-ing into the container to read configuration (docs/ARCHITECTURE-v2.md §3).
    /// </summary>
    private static async Task<IResult> InfoAsync(
        IDatabaseHealthCheck databaseHealth,
        ICacheService cache,
        IEventConsumer consumer,
        ICommentSearchIndex searchIndex,
        IFileStorage storage,
        ActiveProviders providers,
        CancellationToken cancellationToken)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Database] = await ProbeAsync(databaseHealth.CanConnectAsync, cancellationToken),
            [ProviderCatalog.Cache] = Status(cache.IsAvailable),
            [ProviderCatalog.Messaging] = Status(consumer.IsAvailable),
            [ProviderCatalog.Search] = Status(searchIndex.IsAvailable),
            [ProviderCatalog.Storage] = Status(storage.IsAvailable),
        };

        return Results.Ok(new InfoDto
        {
            Version = ApiVersion(),
            Strict = providers.Strict,
            FailFastOnUnavailable = providers.FailFastOnUnavailable,
            Providers = ProviderCatalog.Ports.Select(port =>
            {
                var descriptor = ProviderCatalog.Find(port, providers[port]);
                return new ProviderInfoDto
                {
                    Port = port,
                    Value = providers[port],
                    Status = providers.IsSelfContained(port) ? "disabled" : statuses[port],
                    Implemented = descriptor?.Implemented ?? false,
                    SelfContained = descriptor?.SelfContained ?? false,
                    Description = descriptor?.Description ?? string.Empty,
                    Alternatives = ProviderCatalog.For(port)
                        .Where(p => p.Implemented
                                    && !string.Equals(p.Value, providers[port], StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Value)
                        .ToList(),
                };
            }).ToList(),
        });
    }

    /// <summary>"ok" | "error" — the vocabulary <c>/api/health</c> already uses.</summary>
    private static string Status(bool available) => available ? "ok" : "error";

    private static async Task<string> ProbeAsync(Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        try
        {
            return Status(await probe(cancellationToken));
        }
        catch
        {
            return "error";
        }
    }

    private static IResult Live() => Results.Ok(new { status = "ok" });

    private static async Task<IResult> ReadyAsync(
        IDatabaseHealthCheck databaseHealth,
        CancellationToken cancellationToken)
    {
        var ok = false;
        try
        {
            ok = await databaseHealth.CanConnectAsync(cancellationToken);
        }
        catch
        {
            ok = false;
        }

        return ok
            ? Results.Ok(new { status = "ok", database = "ok" })
            : Results.Json(
                new { status = "unavailable", database = "error" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // ------------------------------------------------------------------ captcha (§2.2)

    private static IResult Captcha(ICaptchaService captchaService)
    {
        var challenge = captchaService.Generate();
        return Results.Ok(new CaptchaDto
        {
            CaptchaId = challenge.CaptchaId,
            Image = challenge.Image,
            ExpiresInSeconds = challenge.ExpiresInSeconds,
        });
    }

    private static IResult CaptchaPeek(string captchaId, ICaptchaService captchaService)
    {
        var code = captchaService.PeekAnswer(captchaId);
        return code is null
            ? ApiErrors.NotFound("Unknown or expired captchaId.")
            : Results.Ok(new CaptchaPeekDto { CaptchaId = captchaId, Code = code });
    }

    // ------------------------------------------------------------------ list (§2.3, §4)

    private static async Task<IResult> ListAsync(
        int? page,
        int? pageSize,
        string? sortBy,
        string? sortDir,
        string? cursor,
        ICommentQueryService queryService,
        ICacheService cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var currentPage = CommentSortOptions.NormalizePage(page);
        var size = CommentSortOptions.NormalizePageSize(pageSize);
        var (normalizedSortBy, normalizedSortDir) = CommentSortOptions.Normalize(sortBy, sortDir);

        // Keyset pages are not cached: the cursor is already an opaque deep-page token.
        if (string.IsNullOrWhiteSpace(cursor))
        {
            var version = await TryGetVersionAsync(cache, loggerFactory, cancellationToken);
            var key = CacheKeys.Page(normalizedSortBy, normalizedSortDir, currentPage, size, version);

            CommentPageDto? cached = null;
            try
            {
                cached = await cache.GetAsync<CommentPageDto>(key, cancellationToken);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("CommentsApi.Cache").LogDebug(ex, "Page cache read failed for {Key}", key);
            }

            if (cached is not null)
            {
                return Results.Ok(cached);
            }

            var result = await queryService.GetRootPageAsync(
                currentPage, size, normalizedSortBy, normalizedSortDir, cancellationToken);

            try
            {
                await cache.SetAsync(key, result, TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("CommentsApi.Cache").LogDebug(ex, "Page cache write failed for {Key}", key);
            }

            return Results.Ok(result);
        }

        try
        {
            var result = await queryService.GetRootPageAsync(
                currentPage, size, normalizedSortBy, normalizedSortDir, cursor, cancellationToken);
            return Results.Ok(result);
        }
        catch (CommentCursorException ex)
        {
            return ApiErrors.BadRequest(ex.Detail);
        }
    }

    private static async Task<long> TryGetVersionAsync(
        ICacheService cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await cache.GetAsync<long?>(CacheKeys.Version, cancellationToken) ?? 0L;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("CommentsApi.Cache")
                .LogDebug(ex, "Cache version read failed; using generation 0");
            return 0L;
        }
    }

    // ------------------------------------------------------------------ get one (§2.4)

    private static async Task<IResult> GetByIdAsync(
        int id,
        ICommentQueryService queryService,
        CancellationToken cancellationToken)
    {
        var comment = await queryService.GetByIdAsync(id, cancellationToken);
        return comment is null ? ApiErrors.NotFound($"Comment {id} not found.") : Results.Ok(comment);
    }

    // ------------------------------------------------------------------ create multipart (§2.5)

    private static async Task<IResult> CreateAsync(
        HttpRequest request,
        CommentCreationFacade facade,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return ApiErrors.MultipartRequired();
        }

        // An empty or malformed multipart body must still produce the standard 400 + errors shape,
        // so fall back to an empty form and let normal validation run.
        IFormCollection form = new FormCollection(new Dictionary<string, StringValues>());
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Keep the empty form; validation reports the missing required fields.
        }

        int? parentId = null;
        string? parentIdError = null;
        var parentIdRaw = form["parentId"].ToString();
        if (!string.IsNullOrWhiteSpace(parentIdRaw))
        {
            if (int.TryParse(parentIdRaw, out var parsedParentId))
            {
                parentId = parsedParentId;
            }
            else
            {
                parentIdError = CommentValidator.ErrorParentInvalid;
            }
        }

        var attachmentFile = form.Files.GetFile("attachment");
        var (clientIp, userAgent) = ClientInfo.Resolve(
            request, loggerFactory.CreateLogger("CommentsApi.ClientInfo"));

        var input = new CommentCreationInput
        {
            UserName = form["userName"].ToString(),
            Email = form["email"].ToString(),
            HomePage = form["homePage"].ToString(),
            Text = form["text"].ToString(),
            CaptchaId = form["captchaId"].ToString(),
            CaptchaAnswer = form["captchaAnswer"].ToString(),
            ParentId = parentId,
            ParentIdError = parentIdError,
            Attachment = attachmentFile is null ? null : ToUploadedFile(attachmentFile),
            ClientIp = clientIp,
            UserAgent = userAgent,
        };

        var result = await facade.CreateAsync(input, cancellationToken);
        if (!result.Success)
        {
            return ApiErrors.Validation(result.Errors!);
        }

        return Results.Created($"/api/comments/{result.Comment!.Id}", new CommentCreatedDto { Comment = result.Comment });
    }

    // ------------------------------------------------------------------ JSON child create (§3.3)

    private static async Task<IResult> CreateChildAsync(
        int id,
        CreateCommentRequest? body,
        HttpRequest request,
        CommentCreationFacade facade,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return ApiErrors.BadRequest("Request body is required.");
        }

        var (clientIp, userAgent) = ClientInfo.Resolve(
            request, loggerFactory.CreateLogger("CommentsApi.ClientInfo"));

        // Parent resolution (docs/API-v2.md §3.3):
        //   * body.parentId wins when present  -> JSON create for a specific parent;
        //   * otherwise the route id is the parent (POST /api/comments/{id}/child);
        //   * route id <= 0 with body.parentId == null creates a ROOT, which is what the k6
        //     JSON-create scenario and the GraphQL mutation need.
        var parentId = body.ParentId ?? (id > 0 ? id : null);

        var input = new CommentCreationInput
        {
            UserName = body.UserName,
            Email = body.Email,
            HomePage = body.HomePage,
            Text = body.Text,
            CaptchaId = body.CaptchaId,
            CaptchaAnswer = body.CaptchaAnswer,
            ParentId = parentId,
            ClientIp = clientIp,
            UserAgent = userAgent,
        };

        var result = await facade.CreateAsync(input, cancellationToken);
        if (!result.Success)
        {
            return ApiErrors.Validation(result.Errors!);
        }

        return Results.Created(
            $"/api/comments/{result.Comment!.Id}",
            new CommentCreatedDto { Comment = result.Comment });
    }

    // ------------------------------------------------------------------ preview (§2.6)

    private static IResult Preview(PreviewRequest? request, HtmlSanitizer sanitizer)
    {
        var text = request?.Text ?? string.Empty;
        var (html, plain, tagErrors) = sanitizer.Sanitize(text);

        var errors = new List<string>();
        if (CommentValidator.ValidateTextLength(text) is { } lengthError)
        {
            errors.Add(lengthError);
        }

        errors.AddRange(tagErrors);

        return Results.Ok(new PreviewResponse
        {
            Valid = errors.Count == 0,
            Html = html,
            Plain = plain,
            Errors = errors.Distinct(StringComparer.Ordinal).ToList(),
        });
    }

    // ------------------------------------------------------------------ search (§3.1)

    private static async Task<IResult> SearchAsync(
        string? q,
        int? page,
        int? pageSize,
        string? sort,
        string? sortDir,
        ICommentSearchIndex searchIndex,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var query = q?.Trim() ?? string.Empty;
        if (query.Length is < 1 or > 200)
        {
            errors.Add("q", "Query must be between 1 and 200 characters.");
        }

        if (!errors.IsValid)
        {
            return ApiErrors.Validation(errors);
        }

        if (!searchIndex.IsEnabled)
        {
            return ApiErrors.ServiceUnavailable("Search unavailable", "Full-text search is disabled.");
        }

        var normalizedPage = CommentSortOptions.NormalizePage(page);
        var normalizedPageSize = CommentSortOptions.NormalizePageSize(pageSize);
        var normalizedSort = NormalizeSearchSort(sort);
        var (_, normalizedSortDir) = CommentSortOptions.Normalize(null, sortDir);

        try
        {
            var result = await searchIndex.SearchAsync(
                new SearchQuery(query, normalizedPage, normalizedPageSize, normalizedSort, normalizedSortDir),
                cancellationToken);

            if (result is null)
            {
                return ApiErrors.ServiceUnavailable("Search unavailable", "Search backend did not respond.");
            }

            return Results.Ok(SearchMapper.ToDto(result));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("CommentsApi.Search").LogWarning(ex, "Search request failed");
            return ApiErrors.ServiceUnavailable("Search unavailable", "Search backend is unavailable.");
        }
    }

    private static string NormalizeSearchSort(string? sort)
        => string.Equals(sort?.Trim(), "createdAt", StringComparison.OrdinalIgnoreCase) ? "createdAt" : "relevance";

    // ------------------------------------------------------------------ stats (§3.5)

    private static async Task<IResult> StatsAsync(
        CommentTotalsProvider totalsProvider,
        ICacheTelemetry cacheTelemetry,
        CancellationToken cancellationToken)
    {
        var totals = await totalsProvider.GetAsync(cancellationToken);
        return Results.Ok(new StatsDto
        {
            TotalComments = totals.TotalComments,
            TotalRoots = totals.TotalRoots,
            TotalAttachments = totals.TotalAttachments,
            OldestAt = totals.OldestAt,
            NewestAt = totals.NewestAt,
            CacheHitRate = cacheTelemetry.HitRate,
        });
    }

    // ------------------------------------------------------------------ seed (§3.4)

    private static async Task<IResult> SeedAsync(
        SeedRequest? request,
        ICommentSeeder seeder,
        ICacheService cache,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var options = request ?? new SeedRequest();
        var errors = new ValidationErrors();

        if (options.Count < 0)
        {
            errors.Add("count", "Count must be >= 0.");
        }

        if (options.Roots < 0)
        {
            errors.Add("roots", "Roots must be >= 0.");
        }

        if (options.Depth < 0)
        {
            errors.Add("depth", "Depth must be >= 0.");
        }

        if (options.BatchSize < 1)
        {
            errors.Add("batchSize", "BatchSize must be >= 1.");
        }

        if (!errors.IsValid)
        {
            return ApiErrors.Validation(errors);
        }

        var outcome = await seeder.SeedAsync(
            new SeedOptions(options.Count, options.Roots, options.Depth, options.BatchSize, options.Clear),
            cancellationToken);

        // A clear/seed changes every page and every counter: drop both and let caches refill.
        var invalidationFailure = await CommentCacheInvalidation.AfterWriteAsync(cache, cancellationToken);
        if (invalidationFailure is not null)
        {
            loggerFactory.CreateLogger("CommentsApi.Cache")
                .LogDebug(invalidationFailure, "Cache invalidation failed after seed");
        }

        var perSecond = outcome.ElapsedMs > 0
            ? Math.Round(outcome.Created / (outcome.ElapsedMs / 1000d), 2)
            : 0d;

        return Results.Ok(new SeedResultDto
        {
            Created = outcome.Created,
            Roots = outcome.Roots,
            ElapsedMs = outcome.ElapsedMs,
            CommentsPerSecond = perSecond,
        });
    }

    // ------------------------------------------------------------------ attachments (§2.7)

    private static async Task<IResult> DownloadAsync(
        int id,
        HttpContext context,
        IAttachmentRepository attachments,
        IAttachmentService attachmentService,
        IFileStorage storage,
        CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetByIdAsync(id, cancellationToken);
        if (attachment is null)
        {
            return ApiErrors.NotFound($"Attachment {id} not found.");
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Disposition"] = BuildInlineDisposition(attachment.FileName);

        var contentType = attachment.Kind == AttachmentKinds.Text
            ? "text/plain; charset=utf-8"
            : attachment.ContentType;

        return await ServeStoredAsync(id, attachment, contentType, attachmentService, storage, cancellationToken);
    }

    private static async Task<IResult> ThumbAsync(
        int id,
        HttpContext context,
        IAttachmentRepository attachments,
        IAttachmentService attachmentService,
        IFileStorage storage,
        CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetByIdAsync(id, cancellationToken);
        if (attachment is null || attachment.Kind != AttachmentKinds.Image)
        {
            return ApiErrors.NotFound($"Attachment {id} has no image thumbnail.");
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Disposition"] = BuildInlineDisposition(attachment.FileName);

        // Stored images are already downscaled to <= 320x240, so the original file is the thumb.
        return await ServeStoredAsync(id, attachment, attachment.ContentType, attachmentService, storage, cancellationToken);
    }

    private static async Task<IResult> ServeStoredAsync(
        int id,
        Attachment attachment,
        string contentType,
        IAttachmentService attachmentService,
        IFileStorage storage,
        CancellationToken cancellationToken)
    {
        // Providers that keep bytes on disk hand out a path (sendfile + range requests); object
        // stores answer false and we stream through the port instead (docs/API-v2.md §11).
        if (attachmentService.TryGetLocalPath(attachment, out var fullPath))
        {
            return File.Exists(fullPath)
                ? Results.File(fullPath, contentType, enableRangeProcessing: true)
                : ApiErrors.NotFound($"Attachment {id} file is missing.");
        }

        var stream = await storage.OpenReadAsync(attachment.StoragePath, cancellationToken);
        return stream is null
            ? ApiErrors.NotFound($"Attachment {id} file is missing.")
            : Results.File(stream, contentType, enableRangeProcessing: true);
    }

    private static string BuildInlineDisposition(string fileName)
    {
        var safe = new string((fileName ?? "file")
            .Where(c => !char.IsControl(c) && c != '"' && c != '\\')
            .ToArray());
        if (safe.Length == 0)
        {
            safe = "file";
        }

        return $"inline; filename=\"{safe}\"";
    }

    // ------------------------------------------------------------------ helpers

    private static UploadedFile ToUploadedFile(IFormFile file) => new()
    {
        FileName = file.FileName,
        ContentType = file.ContentType,
        Length = file.Length,
        OpenReadStream = file.OpenReadStream,
    };

    private static string ApiVersion()
    {
        var assemblyVersion = typeof(Program).Assembly.GetName().Version;
        return assemblyVersion is null || assemblyVersion == new Version(0, 0, 0, 0)
            ? "2.2.0"
            : assemblyVersion.ToString(3);
    }
}
