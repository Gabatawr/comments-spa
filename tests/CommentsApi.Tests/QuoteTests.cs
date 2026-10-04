using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommentsApi.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// Functional quotes end-to-end (docs/DESIGN-v2.1-decisions.md §1, docs/DESIGN-v2.md §4): a reply
/// stores a plain-text snapshot of its parent, exposed as the additive, optional
/// <c>CommentDto.quotedText</c> field over REST and GraphQL alike (all create paths share the
/// facade).
/// </summary>
public class QuoteTests : IntegrationTestBase
{
    [Fact]
    public async Task Root_comment_exposes_null_quoted_text()
    {
        var root = await CreateCommentAsync("QuoteRoot1", email: "qr1@example.com", text: "Root text");

        Assert.True(root.AsObject().ContainsKey("quotedText"), "quotedText must be part of CommentDto");
        Assert.True(root["quotedText"] is null, "a root comment has no quote snapshot");
    }

    [Fact]
    public async Task Reply_snapshot_contains_the_parent_plain_text_without_markup()
    {
        var root = await CreateCommentAsync(
            "QuoteParent1",
            email: "qp1@example.com",
            text: "Hello <strong>world</strong>");

        var reply = await CreateCommentAsync(
            "QuoteChild1",
            email: "qc1@example.com",
            text: "Replying",
            parentId: root["id"]!.GetValue<int>().ToString());

        var quoted = reply["quotedText"]!.GetValue<string>();
        Assert.Equal("Hello world", quoted);
        Assert.DoesNotContain("<", quoted);
        Assert.DoesNotContain(">", quoted);
    }

    [Fact]
    public async Task Quoted_text_is_returned_by_get_comments_tree()
    {
        var root = await CreateCommentAsync("QuoteGet1", email: "qg1@example.com", text: "Parent for GET");
        var rootId = root["id"]!.GetValue<int>();
        var reply = await CreateCommentAsync(
            "QuoteGetChild1",
            email: "qgc1@example.com",
            text: "Child",
            parentId: rootId.ToString());

        var page = await ListAsync();
        var child = Flatten(page["items"]!.AsArray())
            .FirstOrDefault(node => node["id"]!.GetValue<int>() == reply["id"]!.GetValue<int>());

        Assert.NotNull(child);
        Assert.Equal("Parent for GET", child!["quotedText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Long_parent_text_is_truncated_on_a_word_boundary_with_ellipsis()
    {
        var parentText = string.Join(' ', Enumerable.Repeat("word", 50));
        var root = await CreateCommentAsync("QuoteLong1", email: "ql1@example.com", text: parentText);

        var reply = await CreateCommentAsync(
            "QuoteLongChild1",
            email: "qlc1@example.com",
            text: "Child",
            parentId: root["id"]!.GetValue<int>().ToString());

        var quoted = reply["quotedText"]!.GetValue<string>();
        Assert.EndsWith("\u2026", quoted);
        Assert.True(quoted.Length <= 161, $"snapshot must be <= 161 chars, was {quoted.Length}");
        Assert.DoesNotContain("<", quoted);
    }

    [Fact]
    public async Task Raw_script_parent_is_rejected_so_markup_can_never_be_quoted()
    {
        var response = await PostCommentRawAsync(
            userName: "QuoteXss1",
            email: "qx1@example.com",
            text: "<script>alert(1)</script>Hello");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ExpectValidationErrorAsync(response);
        Assert.Contains("text", errors.AsObject().Select(property => property.Key));
    }

    [Fact]
    public async Task Entity_encoded_script_parent_is_stored_and_quoted_as_plain_text()
    {
        // `&lt;script&gt;X&lt;/script&gt;` is text, not a tag: the sanitiser decodes the entities,
        // so textPlain is the literal string "<script>X</script>". The quote snapshots that plain
        // text verbatim; safety comes from escaping at render time (§1.2), not from stripping.
        var root = await CreateCommentAsync(
            "QuoteEntity1",
            email: "qe1@example.com",
            text: "&lt;script&gt;X&lt;/script&gt;");

        Assert.Equal("<script>X</script>", root["textPlain"]!.GetValue<string>());

        var response = await PostCommentRawAsync(
            userName: "QuoteEntityChild1",
            email: "qec1@example.com",
            text: "child",
            parentId: root["id"]!.GetValue<int>().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        // It travels as the *value* of a JSON string property (quoted and escaped as JSON), never
        // as markup; the encoders/defaults keep angle brackets literal.
        Assert.Contains("\"quotedText\":\"<script>X</script>\"", raw);

        var reply = UnwrapComment(await ReadJsonAsync(response));
        Assert.Equal("<script>X</script>", reply["quotedText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Graphql_comment_type_exposes_quoted_text()
    {
        var root = await CreateCommentAsync("QuoteGql1", email: "qgql1@example.com", text: "GraphQL <i>quote</i>");
        var reply = await CreateCommentAsync(
            "QuoteGqlChild1",
            email: "qgqlc1@example.com",
            text: "Child",
            parentId: root["id"]!.GetValue<int>().ToString());

        var payload = JsonSerializer.Serialize(new
        {
            query = $"{{ comment(id: {reply["id"]!.GetValue<int>()}) {{ id quotedText }} }}",
        });

        var response = await Client.PostAsync(
            "/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode, $"POST /graphql -> HTTP {(int)response.StatusCode}");

        var body = await ReadJsonAsync(response);
        Assert.True(body["errors"] is null, body["errors"]?.ToJsonString());
        Assert.Equal("GraphQL quote", body["data"]!["comment"]!["quotedText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Migration_creates_nullable_text_quoted_text_column()
    {
        // Touch the app so the startup migration path ran against this fresh database.
        await CreateCommentAsync("QuoteSchema1", email: "qsch1@example.com", text: "schema check");

        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT data_type, is_nullable FROM information_schema.columns " +
            "WHERE table_name = 'comments' AND column_name = 'quoted_text'";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "comments.quoted_text is missing after migrations");
        Assert.Equal("text", reader.GetString(0));
        Assert.Equal("YES", reader.GetString(1));
    }
}
