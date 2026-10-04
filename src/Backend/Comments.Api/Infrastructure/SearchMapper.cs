using Comments.Application.Abstractions.Search;
using Comments.Application.Dtos;

namespace Comments.Api.Infrastructure;

/// <summary>Maps the Application search result to the REST/GraphQL <see cref="SearchPageDto"/> shape.</summary>
public static class SearchMapper
{
    public static SearchPageDto ToDto(SearchResult result)
    {
        var page = new SearchPageDto
        {
            Query = result.Query,
            Took = result.Took,
            Page = result.Page,
            PageSize = result.PageSize,
            TotalItems = result.TotalItems,
            TotalPages = result.TotalPages,
        };

        foreach (var hit in result.Items)
        {
            // Search hits never carry a reply tree (docs/API-v2.md §1).
            hit.Comment.Replies.Clear();
            hit.Comment.ReplyCount = 0;
            page.Items.Add(new SearchHitDto
            {
                Comment = hit.Comment,
                Score = hit.Score,
                Highlight = hit.Highlight,
            });
        }

        return page;
    }
}
