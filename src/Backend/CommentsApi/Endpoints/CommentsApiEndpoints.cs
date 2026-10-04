using CommentsApi.Data;
using CommentsApi.Domain;
using CommentsApi.Dtos;
using CommentsApi.Infrastructure.Cache;
using CommentsApi.Infrastructure.Queue;
using CommentsApi.Infrastructure.WebSockets;
using CommentsApi.Services;
using CommentsApi.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace CommentsApi.Endpoints;

/// <summary>
/// All HTTP endpoints from docs/API.md. Implemented as minimal APIs so the 400 error shape
/// matches the frozen contract exactly.
/// </summary>
public static class CommentsApiEndpoints
{
    public static void MapCommentsApi(this WebApplication app)
    {
        app.MapGet("/api/health", HealthAsync);
        app.MapGet("/api/captcha", Captcha);
        app.MapGet("/api/comments", ListAsync);
        app.MapGet("/api/comments/{id:int}", GetByIdAsync);
        app.MapPost("/api/comments", CreateAsync);
        app.MapPost("/api/preview", Preview);
        app.MapGet("/api/attachments/{id:int}", DownloadAsync);
        app.MapGet("/api/attachments/{id:int}/thumb", ThumbAsync);
    }

    // ---------------------------------------------------------------- 2.8 health

    private static async Task<IResult> HealthAsync(
        AppDbContext db,
        CommentCreatedQueue queue,
        WebSocketHub webSocketHub,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        var database = "ok";
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                database = "error";
            }
        }
        catch
        {
            database = "error";
        }

        var cacheStatus = "ok";
        try
        {
            var probeKey = $"health:{Guid.NewGuid():N}";
            cache.Set(probeKey, 1, TimeSpan.FromSeconds(5));
            if (!cache.TryGetValue(probeKey, out _))
            {
                cacheStatus = "error";
            }

            cache.Remove(probeKey);
        }
        catch
        {
            cacheStatus = "error";
        }

        var assemblyVersion = typeof(Program).Assembly.GetName().Version;
        var version = assemblyVersion is null || assemblyVersion == new Version(0, 0, 0, 0)
            ? "1.0.0"
            : assemblyVersion.ToString(3);

        return Results.Ok(new HealthDto
        {
            Status = database == "ok" ? "ok" : "degraded",
            Database = database,
            Cache = cacheStatus,
            Queue = new QueueHealthDto { Pending = queue.Pending, Processed = queue.Processed },
            Websocket = new WebSocketHealthDto { Clients = webSocketHub.ClientCount },
            Version = version,
        });
    }

    // ---------------------------------------------------------------- 2.1 captcha

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

    // ---------------------------------------------------------------- 2.2 list

    private static async Task<IResult> ListAsync(
        int? page,
        int? pageSize,
        string? sortBy,
        string? sortDir,
        ICommentQueryService queryService,
        CommentCache cache,
        CancellationToken cancellationToken)
    {
        var currentPage = page is null or < 1 ? 1 : page.Value;
        var size = pageSize switch
        {
            null or < 1 => CommentSortOptions.DefaultPageSize,
            > CommentSortOptions.MaxPageSize => CommentSortOptions.MaxPageSize,
            _ => pageSize.Value,
        };

        var (normalizedSortBy, normalizedSortDir) = CommentSortOptions.Normalize(sortBy, sortDir);
        var key = cache.BuildKey(currentPage, size, normalizedSortBy, normalizedSortDir);

        if (cache.TryGet(key, out CommentPageDto? cached) && cached is not null)
        {
            return Results.Ok(cached);
        }

        var result = await queryService.GetRootPageAsync(currentPage, size, normalizedSortBy, normalizedSortDir, cancellationToken);
        cache.Set(key, result);
        return Results.Ok(result);
    }

    // ---------------------------------------------------------------- 2.3 get one

    private static async Task<IResult> GetByIdAsync(
        int id,
        ICommentQueryService queryService,
        CancellationToken cancellationToken)
    {
        var comment = await queryService.GetByIdAsync(id, cancellationToken);
        return comment is null ? NotFound($"Comment {id} not found.") : Results.Ok(comment);
    }

    // ---------------------------------------------------------------- 2.4 create

    private static async Task<IResult> CreateAsync(
        HttpRequest request,
        AppDbContext db,
        ICaptchaService captchaService,
        HtmlSanitizer sanitizer,
        ICommentCreateService createService,
        IAttachmentService attachmentService,
        CommentCreatedQueue queue,
        CommentCache cache,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.Json(
                new ErrorResponse
                {
                    Title = "Validation failed",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "Request must be multipart/form-data.",
                },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // An empty or malformed multipart body must still produce the standard 400 + errors
        // shape, so fall back to an empty form and let the normal validation run.
        IFormCollection form = new FormCollection(new Dictionary<string, StringValues>());
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Keep the empty form; validation below reports the missing required fields.
        }

        var errors = new ValidationErrors();

        var userName = form["userName"].ToString();
        var email = form["email"].ToString();
        var homePageRaw = form["homePage"].ToString();
        var text = form["text"].ToString();
        var captchaId = form["captchaId"].ToString();
        var captchaAnswer = form["captchaAnswer"].ToString();
        var parentIdRaw = form["parentId"].ToString();

        if (CommentValidator.ValidateUserName(userName) is { } userNameError)
        {
            errors.Add("userName", userNameError);
        }

        if (CommentValidator.ValidateEmail(email) is { } emailError)
        {
            errors.Add("email", emailError);
        }

        if (CommentValidator.ValidateHomePage(homePageRaw, out var homePage) is { } homePageError)
        {
            errors.Add("homePage", homePageError);
        }

        var (html, plain, tagErrors) = sanitizer.Sanitize(text);
        if (CommentValidator.ValidateTextLength(text) is { } textError)
        {
            errors.Add("text", textError);
        }

        errors.AddRange("text", tagErrors);

        int? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentIdRaw))
        {
            if (!int.TryParse(parentIdRaw, out var parsedParentId))
            {
                errors.Add("parentId", "Parent comment id is not valid.");
            }
            else
            {
                parentId = parsedParentId;
                if (!await db.Comments.AnyAsync(c => c.Id == parsedParentId, cancellationToken))
                {
                    errors.Add("parentId", CommentValidator.ErrorParentNotFound);
                }
            }
        }

        PreparedAttachment? preparedAttachment = null;
        var attachmentFile = form.Files.GetFile("attachment");
        if (attachmentFile is not null)
        {
            var validation = await attachmentService.ValidateAsync(attachmentFile, cancellationToken);
            if (validation.IsValid)
            {
                preparedAttachment = validation.Prepared;
            }
            else
            {
                errors.Add("attachment", validation.Error ?? AttachmentService.ErrorUnsupported);
            }
        }

        // The CAPTCHA is consumed only once the rest of the form is valid, so a typo does not
        // force the user to reload the image.
        if (errors.IsValid)
        {
            if (string.IsNullOrWhiteSpace(captchaId) || string.IsNullOrWhiteSpace(captchaAnswer))
            {
                errors.Add("captcha", CommentValidator.ErrorCaptchaRequired);
            }
            else if (!captchaService.Validate(captchaId, captchaAnswer))
            {
                errors.Add("captcha", CommentValidator.ErrorCaptchaInvalid);
            }
        }

        if (!errors.IsValid)
        {
            return Results.Json(errors.ToResponse(), statusCode: StatusCodes.Status400BadRequest);
        }

        int? attachmentId = null;
        if (preparedAttachment is not null)
        {
            var stored = await attachmentService.PersistAsync(preparedAttachment, cancellationToken);
            attachmentId = stored.Id;
        }

        var clientIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            userAgent = null;
        }
        else if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        var dto = await createService.CreateAsync(
            new CommentCreateModel
            {
                UserName = userName,
                Email = email,
                HomePage = homePage,
                TextHtml = html,
                TextPlain = plain,
                ParentId = parentId,
                AttachmentId = attachmentId,
                ClientIp = clientIp,
                UserAgent = userAgent,
            },
            cancellationToken);

        // Invalidate the page cache synchronously so a read right after the write is consistent;
        // the queue/event path below still publishes for WebSocket broadcast and logging.
        cache.Invalidate();

        // Fire-and-forget: the response does not wait for cache/WebSocket/log processing.
        queue.Enqueue(new CommentCreatedNotification(dto));

        return Results.Created($"/api/comments/{dto.Id}", new CommentCreatedDto { Comment = dto });
    }

    // ---------------------------------------------------------------- 2.5 preview

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

    // ---------------------------------------------------------------- 2.6 attachments

    private static async Task<IResult> DownloadAsync(
        int id,
        HttpContext context,
        AppDbContext db,
        IAttachmentService attachmentService,
        CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null)
        {
            return NotFound($"Attachment {id} not found.");
        }

        var fullPath = attachmentService.GetFullPath(attachment);
        if (!File.Exists(fullPath))
        {
            return NotFound($"Attachment {id} file is missing.");
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Disposition"] = BuildInlineDisposition(attachment.FileName);

        var contentType = attachment.Kind == AttachmentKinds.Text
            ? "text/plain; charset=utf-8"
            : attachment.ContentType;

        return Results.File(fullPath, contentType, enableRangeProcessing: true);
    }

    // ---------------------------------------------------------------- 2.7 thumbs

    private static async Task<IResult> ThumbAsync(
        int id,
        HttpContext context,
        AppDbContext db,
        IAttachmentService attachmentService,
        CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null || attachment.Kind != AttachmentKinds.Image)
        {
            return NotFound($"Attachment {id} has no image thumbnail.");
        }

        var fullPath = attachmentService.GetFullPath(attachment);
        if (!File.Exists(fullPath))
        {
            return NotFound($"Attachment {id} file is missing.");
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Disposition"] = BuildInlineDisposition(attachment.FileName);

        // Stored images are already downscaled to <= 320x240, so the original file is the thumb.
        return Results.File(fullPath, attachment.ContentType, enableRangeProcessing: true);
    }

    private static IResult NotFound(string detail) => Results.Json(
        new ErrorResponse { Title = "Not Found", Status = StatusCodes.Status404NotFound, Detail = detail },
        statusCode: StatusCodes.Status404NotFound);

    private static string BuildInlineDisposition(string fileName)
    {
        var safe = new string((fileName ?? "file").Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray());
        if (safe.Length == 0)
        {
            safe = "file";
        }

        return $"inline; filename=\"{safe}\"";
    }
}
