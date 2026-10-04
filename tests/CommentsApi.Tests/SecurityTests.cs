using System.Net;
using System.Text.Json.Nodes;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>XSS and SQL-injection resistance (TASK.md main-page items 4; raw p.1, p.4).</summary>
public class SecurityTests : IntegrationTestBase
{
    [Fact]
    public async Task Script_in_text_is_never_stored()
    {
        var response = await PostCommentRawAsync(
            userName: "XssUser01",
            email: "xss1@example.com",
            text: "<script>alert(1)</script><i>x</i>");

        // Either the sanitizer strips the script (201) or the request is rejected (400);
        // in both cases nothing executable may be persisted.
        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest,
            $"unexpected HTTP {(int)response.StatusCode}");

        var list = await ListAsync(pageSize: 100);
        foreach (var item in Flatten(list["items"]!.AsArray()))
        {
            Assert.DoesNotContain("<script", item["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", item["textPlain"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Attribute_based_xss_is_never_stored()
    {
        var payloads = new[]
        {
            "<a href=\"javascript:alert(1)\">click</a>",
            "<a href=\"JaVaScRiPt:alert(1)\">click</a>",
            "<i onmouseover=\"alert(1)\">x</i>",
            "<strong style=\"background:url(javascript:alert(1))\">x</strong>",
        };

        foreach (var payload in payloads)
        {
            var response = await PostCommentRawAsync(
                userName: "XssUser02",
                email: "xss2@example.com",
                text: payload);

            Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest,
                $"payload '{payload}' -> HTTP {(int)response.StatusCode}");
        }

        var list = await ListAsync(pageSize: 100);
        foreach (var item in Flatten(list["items"]!.AsArray()))
        {
            var text = item["text"]!.GetValue<string>();
            Assert.DoesNotContain("javascript:", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onmouseover", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onerror", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onclick", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("userName", "<script>alert(1)</script>")]
    [InlineData("userName", "\"><script>alert(1)</script>")]
    [InlineData("email", "\"><script>alert(1)</script>@x.com")]
    [InlineData("homePage", "javascript:alert(1)")]
    [InlineData("homePage", "\"><script>alert(1)</script>")]
    public async Task Xss_in_other_fields_is_rejected(string field, string payload)
    {
        var response = field switch
        {
            "userName" => await PostCommentRawAsync(userName: payload),
            "email" => await PostCommentRawAsync(email: payload),
            _ => await PostCommentRawAsync(homePage: payload),
        };

        var errors = await ExpectValidationErrorAsync(response);
        Assert.NotNull(errors[field]);
    }

    [Fact]
    public async Task Stored_fields_are_escaped_in_the_list_payload()
    {
        await CreateCommentAsync("EscUser001", email: "esc@example.com", homePage: "https://example.com/?a=1&b=2");
        var list = await ListAsync();

        // The JSON payload itself must not contain a raw executable fragment.
        var raw = list.ToJsonString();
        Assert.DoesNotContain("<script", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sqli_in_form_fields_does_not_break_the_database()
    {
        // userName is constrained by the regex -> rejected.
        var u = await PostCommentRawAsync(userName: "' OR 1=1 --");
        Assert.Equal(HttpStatusCode.BadRequest, u.StatusCode);

        // email with quote -> invalid email -> rejected.
        var e = await PostCommentRawAsync(email: "a' OR '1'='1@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, e.StatusCode);

        // text is free-form: stored as data, must not execute.
        var t = await PostCommentRawAsync(userName: "SqliText01", email: "sqli1@example.com",
            text: "'; DROP TABLE Comments; --");
        Assert.Equal(HttpStatusCode.Created, t.StatusCode);

        var list = await ListAsync();
        Assert.Equal(1, list["totalItems"]!.GetValue<int>());
        Assert.Contains("DROP TABLE", list["items"]![0]!["textPlain"]!.GetValue<string>());

        var health = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("sortBy", "userName;DROP TABLE Comments;--")]
    [InlineData("sortBy", "' OR 1=1 --")]
    [InlineData("sortBy", "createdAt'--")]
    [InlineData("sortDir", "asc;DROP TABLE Comments")]
    [InlineData("sortDir", "' OR '1'='1")]
    [InlineData("page", "1;DROP TABLE Comments")]
    [InlineData("page", "' OR 1=1 --")]
    [InlineData("pageSize", "25;DROP TABLE Comments")]
    public async Task Sqli_in_query_params_is_safe(string param, string value)
    {
        var url = $"/api/comments?{param}={Uri.EscapeDataString(value)}";
        var response = await Client.GetAsync(url);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"GET {url} -> HTTP {(int)response.StatusCode}");

        // Database still alive and readable afterwards.
        var list = await ListAsync();
        Assert.NotNull(list["items"]);
        var health = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9999999999999999999999")]
    public async Task Extreme_pagination_values_are_bounded(string pageSize)
    {
        var response = await Client.GetAsync($"/api/comments?pageSize={Uri.EscapeDataString(pageSize)}");

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"pageSize={pageSize} -> HTTP {(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var node = await ReadJsonAsync(response);
            var effective = node["pageSize"]!.GetValue<int>();
            Assert.InRange(effective, 1, 100);
        }
    }

    [Theory]
    [InlineData("1'; DROP TABLE Comments;--")]
    [InlineData("1 OR 1=1")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public async Task Sqli_or_malformed_parentId_is_rejected_and_db_survives(string parentId)
    {
        var response = await PostCommentRawAsync(userName: "SqliParent1", email: "sqli2@example.com",
            parentId: parentId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var list = await ListAsync();
        Assert.NotNull(list["items"]);
        var health = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Unicode_bom_and_emoji_never_crash_the_api()
    {
        var unicodeText = "\uFEFFПривет 🚀 مرحبا \u200F <i>ok</i>";
        var r1 = await PostCommentRawAsync(userName: "UniUser01", email: "uni1@example.com", text: unicodeText);
        Assert.True(
            r1.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest,
            $"unicode text -> HTTP {(int)r1.StatusCode}");

        // ASP.NET Core's multipart form reader consumes a leading UTF-8 BOM as an encoding
        // marker, so a BOM-only prefix is stripped and the remaining latin username is valid.
        // Accepted behaviour, arbitrated by the Lead (see docs/qa/report.md).
        var r2 = await PostCommentRawAsync(userName: "\uFEFFUniUser02", email: "uni2@example.com");
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var stored = UnwrapComment(await ReadJsonAsync(r2))["userName"]!.GetValue<string>();
        Assert.Equal("UniUser02", stored);
        Assert.DoesNotContain('\uFEFF', stored);

        // A BOM/format character anywhere else must be rejected by the charset rule.
        foreach (var value in new[] { "\uFEFF UniUser03", "Uni\uFEFFUser03", "Uni\u200FUser03" })
        {
            var rejected = await PostCommentRawAsync(userName: value, email: "uni3@example.com");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var rejectedBody = await ReadJsonAsync(rejected);
            Assert.NotNull(rejectedBody["errors"]!["userName"]);
        }

        var health = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Empty_multipart_body_is_rejected()
    {
        using var form = new MultipartFormDataContent();
        var response = await Client.PostAsync("/api/comments", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(
            body["errors"] is not null || body["detail"] is not null,
            "400 response must carry either the validation errors object or a detail message");
    }

    [Fact]
    public async Task Xss_payload_in_query_string_is_not_reflected_unescaped()
    {
        var response = await Client.GetAsync("/api/comments?sortBy=%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);
        Assert.DoesNotContain("<script>alert(1)</script>", body, StringComparison.OrdinalIgnoreCase);
    }
}
