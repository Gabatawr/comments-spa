using Comments.Api.Infrastructure;
using Comments.Application.Abstractions.Search;
using Comments.Application.Dtos;
using Comments.Application.Services;
using Comments.Application.Validation;
using HotChocolate;

namespace Comments.Api.GraphQL;

/// <summary>GraphQL <c>Query</c> (docs/API-v2.md §5). Delegates to the same Application services as REST.</summary>
public sealed class Query
{
    public async Task<CommentPageDto> Comments(
        int page = 1,
        int pageSize = 25,
        CommentSortField sortBy = CommentSortField.CreatedAt,
        SortDirection sortDir = SortDirection.Desc,
        string? cursor = null,
        [Service] ICommentQueryService queryService = null!,
        CancellationToken cancellationToken = default)
        => await queryService.GetRootPageAsync(
            page,
            pageSize,
            sortBy.ToRest(),
            sortDir.ToRest(),
            cursor,
            cancellationToken);

    public async Task<CommentDto?> Comment(
        long id,
        [Service] ICommentQueryService queryService = null!,
        CancellationToken cancellationToken = default)
        => id is < int.MinValue or > int.MaxValue
            ? null
            : await queryService.GetByIdAsync((int)id, cancellationToken);

    public async Task<SearchPageDto> Search(
        string query,
        int page = 1,
        int pageSize = 25,
        [Service] ICommentSearchIndex searchIndex = null!,
        CancellationToken cancellationToken = default)
    {
        if (!searchIndex.IsEnabled)
        {
            throw new GraphQLException("Search unavailable.");
        }

        var result = await searchIndex.SearchAsync(
            new SearchQuery(query, page, pageSize, "relevance", "desc"),
            cancellationToken);

        return SearchMapper.ToDto(result);
    }
}

/// <summary>GraphQL <c>Mutation</c> (docs/API-v2.md §5). Reuses the exact REST create pipeline.</summary>
public sealed class Mutation
{
    public async Task<CreateCommentPayload> CreateComment(
        CreateCommentRequest input,
        [Service] CommentCreationFacade facade = null!,
        [Service] IHttpContextAccessor httpContextAccessor = null!,
        CancellationToken cancellationToken = default)
    {
        var request = httpContextAccessor.HttpContext?.Request;
        var (clientIp, userAgent) = request is null ? (null, null) : ClientInfo.Resolve(request);

        var result = await facade.CreateAsync(
            new CommentCreationInput
            {
                UserName = input.UserName,
                Email = input.Email,
                HomePage = input.HomePage,
                Text = input.Text,
                ParentId = input.ParentId,
                CaptchaId = input.CaptchaId,
                CaptchaAnswer = input.CaptchaAnswer,
                ClientIp = clientIp,
                UserAgent = userAgent,
            },
            cancellationToken);

        return result.Success
            ? new CreateCommentPayload { Comment = result.Comment, Success = true }
            : new CreateCommentPayload { Comment = null, Success = false, Errors = ToErrors(result.Errors!) };
    }

    private static IReadOnlyList<ValidationErrorDto> ToErrors(ValidationErrors errors)
    {
        var list = new List<ValidationErrorDto>();
        foreach (var (field, messages) in errors.Errors)
        {
            foreach (var message in messages)
            {
                list.Add(new ValidationErrorDto { Field = field, Message = message });
            }
        }

        return list;
    }
}
