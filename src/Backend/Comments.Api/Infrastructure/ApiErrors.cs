using Comments.Application.Dtos;
using Comments.Application.Validation;

namespace Comments.Api.Infrastructure;

/// <summary>
/// Response helpers that keep the frozen error shapes of docs/API-v2.md §0:
/// validation -> <c>{title,status,errors}</c>; everything else -> <c>{title,status,detail}</c>.
/// </summary>
public static class ApiErrors
{
    public static IResult Validation(ValidationErrors errors)
        => Results.Json(errors.ToResponse(), statusCode: StatusCodes.Status400BadRequest);

    public static IResult BadRequest(string detail)
        => Results.Json(
            new ErrorResponse { Title = "Bad Request", Status = StatusCodes.Status400BadRequest, Detail = detail },
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>v1-parity 400 for a non-multipart body (title is "Validation failed", not "Bad Request").</summary>
    public static IResult MultipartRequired()
        => Results.Json(
            new ErrorResponse
            {
                Title = "Validation failed",
                Status = StatusCodes.Status400BadRequest,
                Detail = "Request must be multipart/form-data.",
            },
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotFound(string detail)
        => Results.Json(
            new ErrorResponse { Title = "Not Found", Status = StatusCodes.Status404NotFound, Detail = detail },
            statusCode: StatusCodes.Status404NotFound);

    public static IResult ServiceUnavailable(string title, string? detail = null)
        => Results.Json(
            new ErrorResponse { Title = title, Status = StatusCodes.Status503ServiceUnavailable, Detail = detail },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>Cache key layout from docs/API-v2.md §9.</summary>
public static class CommentCacheKeys
{
    /// <summary>Generation counter; bumped on every CommentCreated to invalidate all pages at once.</summary>
    public const string Version = "comments:version";

    public static string Page(string sortBy, string sortDir, int page, int pageSize, long version)
        => $"comments:page:{sortBy}:{sortDir}:{page}:{pageSize}:v{version}";

    public static string Item(int id) => $"comments:item:{id}";
}
