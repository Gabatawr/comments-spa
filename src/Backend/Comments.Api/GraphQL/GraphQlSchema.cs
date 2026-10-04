using Comments.Application.Dtos;
using HotChocolate.Types;

namespace Comments.Api.GraphQL;

/// <summary>Sort field of the GraphQL <c>comments</c> query (docs/API-v2.md §5).</summary>
public enum CommentSortField
{
    CreatedAt,
    UserName,
    Email,
}

/// <summary>Sort direction of the GraphQL <c>comments</c> query (docs/API-v2.md §5).</summary>
public enum SortDirection
{
    Asc,
    Desc,
}

/// <summary>Bridges the GraphQL enums and the REST string values used by the Application services.</summary>
public static class CommentSortMapping
{
    public static string ToRest(this CommentSortField field) => field switch
    {
        CommentSortField.UserName => "userName",
        CommentSortField.Email => "email",
        _ => "createdAt",
    };

    public static string ToRest(this SortDirection direction)
        => direction == SortDirection.Asc ? "asc" : "desc";

    public static CommentSortField FieldFromRest(string? value)
        => value switch
        {
            "userName" => CommentSortField.UserName,
            "email" => CommentSortField.Email,
            _ => CommentSortField.CreatedAt,
        };

    public static SortDirection DirectionFromRest(string? value)
        => value == "asc" ? SortDirection.Asc : SortDirection.Desc;
}

/// <summary>Result of the <c>createComment</c> mutation (docs/API-v2.md §5).</summary>
public sealed class CreateCommentPayload
{
    public CommentDto? Comment { get; init; }

    public IReadOnlyList<ValidationErrorDto> Errors { get; init; } = Array.Empty<ValidationErrorDto>();

    public bool Success { get; init; }
}

/// <summary>GraphQL type <c>Comment</c>: DTO shape with <c>Long</c> ids (docs/API-v2.md §5).</summary>
public sealed class CommentType : ObjectType<CommentDto>
{
    protected override void Configure(IObjectTypeDescriptor<CommentDto> descriptor)
    {
        descriptor.Name("Comment");
        descriptor.Field(t => t.Id).Type<NonNullType<LongType>>();
        descriptor.Field(t => t.ParentId).Type<LongType>();
        descriptor.Field(t => t.ReplyCount).Type<NonNullType<IntType>>();
    }
}

/// <summary>GraphQL type <c>Attachment</c>.</summary>
public sealed class AttachmentType : ObjectType<AttachmentDto>
{
    protected override void Configure(IObjectTypeDescriptor<AttachmentDto> descriptor)
    {
        descriptor.Name("Attachment");
        descriptor.Field(t => t.Id).Type<NonNullType<LongType>>();
        descriptor.Field(t => t.Width).Type<IntType>();
        descriptor.Field(t => t.Height).Type<IntType>();
    }
}

/// <summary>GraphQL type <c>CommentPage</c> (docs/API-v2.md §5).</summary>
public sealed class CommentPageType : ObjectType<CommentPageDto>
{
    protected override void Configure(IObjectTypeDescriptor<CommentPageDto> descriptor)
    {
        descriptor.Name("CommentPage");
        descriptor.Field(t => t.SortBy)
            .Type<NonNullType<EnumType<CommentSortField>>>()
            .Resolve(context => CommentSortMapping.FieldFromRest(context.Parent<CommentPageDto>().SortBy));
        descriptor.Field(t => t.SortDir)
            .Type<NonNullType<EnumType<SortDirection>>>()
            .Resolve(context => CommentSortMapping.DirectionFromRest(context.Parent<CommentPageDto>().SortDir));
    }
}

/// <summary>GraphQL type <c>SearchHit</c>.</summary>
public sealed class SearchHitType : ObjectType<SearchHitDto>
{
    protected override void Configure(IObjectTypeDescriptor<SearchHitDto> descriptor)
    {
        descriptor.Name("SearchHit");
        descriptor.Field(t => t.Score).Type<NonNullType<FloatType>>();
        descriptor.Field(t => t.Highlight).Type<StringType>();
    }
}

/// <summary>GraphQL type <c>SearchPage</c>.</summary>
public sealed class SearchPageType : ObjectType<SearchPageDto>
{
    protected override void Configure(IObjectTypeDescriptor<SearchPageDto> descriptor)
    {
        descriptor.Name("SearchPage");
        descriptor.Field(t => t.Took).Type<NonNullType<IntType>>();
        descriptor.Field(t => t.Items).Type<NonNullType<ListType<NonNullType<SearchHitType>>>>();
    }
}

/// <summary>GraphQL type <c>ValidationError</c>.</summary>
public sealed class ValidationErrorType : ObjectType<ValidationErrorDto>
{
    protected override void Configure(IObjectTypeDescriptor<ValidationErrorDto> descriptor)
    {
        descriptor.Name("ValidationError");
        descriptor.Field(t => t.Field).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.Message).Type<NonNullType<StringType>>();
    }
}

/// <summary>GraphQL input <c>CreateCommentInput</c> (docs/API-v2.md §5).</summary>
public sealed class CreateCommentInputType : InputObjectType<CreateCommentRequest>
{
    protected override void Configure(IInputObjectTypeDescriptor<CreateCommentRequest> descriptor)
    {
        descriptor.Name("CreateCommentInput");
        descriptor.Field(t => t.UserName).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.Email).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.HomePage).Type<StringType>();
        descriptor.Field(t => t.Text).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.ParentId).Type<LongType>();
        descriptor.Field(t => t.CaptchaId).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.CaptchaAnswer).Type<NonNullType<StringType>>();
    }
}

/// <summary>GraphQL type <c>CreateCommentPayload</c> (docs/API-v2.md §5).</summary>
public sealed class CreateCommentPayloadType : ObjectType<CreateCommentPayload>
{
    protected override void Configure(IObjectTypeDescriptor<CreateCommentPayload> descriptor)
    {
        descriptor.Name("CreateCommentPayload");
        descriptor.Field(t => t.Errors)
            .Type<NonNullType<ListType<NonNullType<ValidationErrorType>>>>();
        descriptor.Field(t => t.Success).Type<NonNullType<BooleanType>>();
    }
}
