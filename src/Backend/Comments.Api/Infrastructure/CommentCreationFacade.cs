using Comments.Application.Abstractions.Caching;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Abstractions.Persistence;
using Comments.Application.Dtos;
using Comments.Application.Services;
using Comments.Application.Validation;
using Comments.Domain;

namespace Comments.Api.Infrastructure;

/// <summary>
/// Raw, unvalidated create request. All three create paths (multipart REST, JSON child REST and
/// the GraphQL mutation) funnel through here so validation, CAPTCHA, attachment handling, cache
/// invalidation and event publication are byte-for-byte identical.
/// </summary>
public sealed class CommentCreationInput
{
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public string? HomePage { get; init; }
    public string? Text { get; init; }

    /// <summary>Already-parsed parent id (route id for the JSON child endpoint).</summary>
    public int? ParentId { get; init; }

    /// <summary>Set when the raw parentId was present but not a valid integer (multipart only).</summary>
    public string? ParentIdError { get; init; }

    public string? CaptchaId { get; init; }
    public string? CaptchaAnswer { get; init; }
    public UploadedFile? Attachment { get; init; }
    public string? ClientIp { get; init; }
    public string? UserAgent { get; init; }
}

/// <summary>Either a persisted comment or the accumulated 400-shaped validation errors.</summary>
public sealed class CommentCreationResult
{
    public CommentDto? Comment { get; init; }
    public ValidationErrors? Errors { get; init; }

    public bool Success => Comment is not null;

    public bool IsValidationFailure => !Success && Errors is { IsValid: false };
}

/// <summary>
/// Shared create pipeline (docs/API-v2.md §2.5, §3.3, §5). The order of checks mirrors v1 exactly:
/// field validation first, CAPTCHA last and only when the rest is valid (so typos do not consume
/// the challenge), and nothing is persisted when there is any error.
/// </summary>
public sealed class CommentCreationFacade
{
    private readonly ICommentCreateService _createService;
    private readonly ICommentRepository _repository;
    private readonly IAttachmentService _attachmentService;
    private readonly ICaptchaService _captchaService;
    private readonly HtmlSanitizer _sanitizer;
    private readonly IEventPublisher _eventPublisher;
    private readonly ICacheService _cache;
    private readonly ILogger<CommentCreationFacade> _logger;

    public CommentCreationFacade(
        ICommentCreateService createService,
        ICommentRepository repository,
        IAttachmentService attachmentService,
        ICaptchaService captchaService,
        HtmlSanitizer sanitizer,
        IEventPublisher eventPublisher,
        ICacheService cache,
        ILogger<CommentCreationFacade> logger)
    {
        _createService = createService;
        _repository = repository;
        _attachmentService = attachmentService;
        _captchaService = captchaService;
        _sanitizer = sanitizer;
        _eventPublisher = eventPublisher;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CommentCreationResult> CreateAsync(
        CommentCreationInput input,
        CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();

        if (CommentValidator.ValidateUserName(input.UserName) is { } userNameError)
        {
            errors.Add("userName", userNameError);
        }

        if (CommentValidator.ValidateEmail(input.Email) is { } emailError)
        {
            errors.Add("email", emailError);
        }

        if (CommentValidator.ValidateHomePage(input.HomePage, out var homePage) is { } homePageError)
        {
            errors.Add("homePage", homePageError);
        }

        var (html, plain, tagErrors) = _sanitizer.Sanitize(input.Text);
        if (CommentValidator.ValidateTextLength(input.Text) is { } textError)
        {
            errors.Add("text", textError);
        }

        errors.AddRange("text", tagErrors);

        // The parent is loaded once: it both proves existence and supplies the flat text for the
        // functional quote snapshot (docs/DESIGN-v2.1-decisions.md §1).
        Comment? parent = null;
        if (!string.IsNullOrEmpty(input.ParentIdError))
        {
            errors.Add("parentId", input.ParentIdError);
        }
        else if (input.ParentId is { } parentId)
        {
            parent = await _repository.GetByIdAsync(parentId, cancellationToken);
            if (parent is null)
            {
                errors.Add("parentId", CommentValidator.ErrorParentNotFound);
            }
        }

        PreparedAttachment? preparedAttachment = null;
        if (input.Attachment is not null)
        {
            var validation = await _attachmentService.ValidateAsync(input.Attachment, cancellationToken);
            if (validation.IsValid)
            {
                preparedAttachment = validation.Prepared;
            }
            else
            {
                errors.Add("attachment", validation.Error ?? AttachmentErrors.Unsupported);
            }
        }

        // The CAPTCHA is consumed only once the rest of the input is valid.
        if (errors.IsValid)
        {
            if (string.IsNullOrWhiteSpace(input.CaptchaId) || string.IsNullOrWhiteSpace(input.CaptchaAnswer))
            {
                errors.Add("captcha", CommentValidator.ErrorCaptchaRequired);
            }
            else if (!_captchaService.Validate(input.CaptchaId, input.CaptchaAnswer))
            {
                errors.Add("captcha", CommentValidator.ErrorCaptchaInvalid);
            }
        }

        if (!errors.IsValid)
        {
            return new CommentCreationResult { Errors = errors };
        }

        int? attachmentId = null;
        Attachment? storedAttachment = null;
        if (preparedAttachment is not null)
        {
            storedAttachment = await _attachmentService.PersistAsync(preparedAttachment, cancellationToken);
            attachmentId = storedAttachment.Id;
        }

        CommentDto dto;
        try
        {
            dto = await _createService.CreateAsync(
                new CommentCreateModel
                {
                    UserName = input.UserName ?? string.Empty,
                    Email = input.Email ?? string.Empty,
                    HomePage = homePage,
                    TextHtml = html,
                    TextPlain = plain,
                    QuotedText = parent is null ? null : QuoteSnapshot.FromPlainText(parent.TextPlain),
                    ParentId = input.ParentId,
                    AttachmentId = attachmentId,
                    ClientIp = input.ClientIp,
                    UserAgent = input.UserAgent,
                },
                cancellationToken);
        }
        catch
        {
            // The bytes are already in storage and the metadata row is already committed, so a
            // failure here would leave an attachment nothing can reach: no comment references it,
            // and neither the filesystem nor an object store can tell it apart from a live one.
            // Undo the upload before letting the error out (docs/ARCHITECTURE-v2.md §5).
            if (storedAttachment is not null)
            {
                await _attachmentService.DeleteAsync(storedAttachment, CancellationToken.None);
            }

            throw;
        }

        // Synchronous invalidation gives the instance that served this POST read-after-write
        // consistency; the cache projection repeats it over the broker so the other replicas drop
        // their own cached pages too (docs/API-v2.md §9). Same helper, so the two cannot drift.
        var invalidationFailure = await CommentCacheInvalidation.AfterWriteAsync(_cache, cancellationToken);
        if (invalidationFailure is not null)
        {
            _logger.LogDebug(
                invalidationFailure,
                "Cache invalidation failed after creating comment #{CommentId}",
                dto.Id);
        }

        try
        {
            await _eventPublisher.PublishAsync(
                new DomainEventEnvelope
                {
                    EventType = "CommentCreated",
                    Payload = new CommentCreatedPayload(dto),
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The comment is already committed; a publish hiccup must not turn a 201 into a 500.
            _logger.LogWarning(ex, "Failed to publish CommentCreated for comment #{CommentId}", dto.Id);
        }

        return new CommentCreationResult { Comment = dto };
    }
}