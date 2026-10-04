using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// Base class for HTTP-level integration tests. Every test instance builds its own
/// <see cref="TestAppFactory"/> (own temp SQLite DB + storage dir), so tests are isolated.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected TestAppFactory Factory { get; }
    protected HttpClient Client { get; }

    protected IntegrationTestBase()
    {
        Factory = new TestAppFactory();
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("CommentsQA/1.0");
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ JSON

    protected static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(body)
               ?? throw new InvalidOperationException($"Empty JSON body (HTTP {(int)response.StatusCode}).");
    }

    protected static JsonNode UnwrapComment(JsonNode node)
        => node["comment"] is { } inner ? inner : node;

    protected static IEnumerable<JsonNode> Flatten(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            yield return item;
            if (item["replies"] is JsonArray children)
            {
                foreach (var child in Flatten(children))
                {
                    yield return child;
                }
            }
        }
    }

    // --------------------------------------------------------------- CAPTCHA

    /// <summary>Issues a CAPTCHA and returns (captchaId, code) using the dev peek endpoint.</summary>
    protected async Task<(string Id, string Code)> NewCaptchaAsync()
    {
        var response = await Client.GetAsync("/api/captcha");
        Assert.True(response.IsSuccessStatusCode, $"/api/captcha -> HTTP {(int)response.StatusCode}");
        var node = await ReadJsonAsync(response);
        var id = node["captchaId"]?.GetValue<string>()
                 ?? throw new InvalidOperationException("/api/captcha response is missing captchaId.");

        var peek = await Client.GetAsync($"/api/dev/captcha/{Uri.EscapeDataString(id)}");
        if (peek.IsSuccessStatusCode)
        {
            var peeked = await ReadJsonAsync(peek);
            var code = peeked["code"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("/api/dev/captcha response is missing code.");
            return (id, code);
        }

        var viaService = PeekViaService(id);
        if (viaService is null)
        {
            throw new InvalidOperationException(
                $"Cannot solve CAPTCHA: /api/dev/captcha -> HTTP {(int)peek.StatusCode} and " +
                "ICaptchaService.PeekAnswer(id) was not resolvable.");
        }

        return (id, viaService);
    }

    /// <summary>
    /// Fallback CAPTCHA solver that resolves the public ICaptchaService from DI by reflection,
    /// so the test project does not hard-depend on the backend's namespaces.
    /// </summary>
    private string? PeekViaService(string id)
    {
        try
        {
            var serviceType = typeof(Program).Assembly.GetTypes()
                .FirstOrDefault(t => t.IsInterface && t.Name == "ICaptchaService");
            if (serviceType is null)
            {
                return null;
            }

            var service = Factory.Services.GetService(serviceType);
            if (service is null)
            {
                return null;
            }

            var method = serviceType.GetMethod("PeekAnswer") ?? service.GetType().GetMethod("PeekAnswer");
            if (method is null)
            {
                return null;
            }

            var result = method.Invoke(service, new object?[] { id });
            switch (result)
            {
                case null:
                    return null;
                case string s:
                    return s;
                default:
                    return result.GetType().GetProperty("Code")?.GetValue(result)?.ToString()
                           ?? result.GetType().GetProperty("Answer")?.GetValue(result)?.ToString()
                           ?? result.ToString();
            }
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------- POST create

    /// <summary>POST /api/comments as multipart/form-data. Set omit* flags to test missing fields.</summary>
    protected async Task<HttpResponseMessage> PostCommentRawAsync(
        string? userName = "QaUser1",
        string? email = "qa@example.com",
        string? homePage = null,
        string? text = "Hello <i>world</i>",
        string? captchaId = null,
        string? captchaAnswer = null,
        string? parentId = null,
        byte[]? fileBytes = null,
        string? fileName = null,
        string? fileContentType = null,
        bool omitUserName = false,
        bool omitEmail = false,
        bool omitText = false,
        bool omitCaptcha = false)
    {
        if (!omitCaptcha && captchaId is null)
        {
            var (id, code) = await NewCaptchaAsync();
            captchaId = id;
            captchaAnswer ??= code;
        }

        using var form = new MultipartFormDataContent();

        void AddText(string name, string? value)
        {
            if (value is not null)
            {
                form.Add(new StringContent(value, Encoding.UTF8), name);
            }
        }

        if (!omitUserName)
        {
            AddText("userName", userName);
        }

        if (!omitEmail)
        {
            AddText("email", email);
        }

        if (homePage is not null)
        {
            AddText("homePage", homePage);
        }

        if (!omitText)
        {
            AddText("text", text);
        }

        if (!omitCaptcha)
        {
            AddText("captchaId", captchaId);
            AddText("captchaAnswer", captchaAnswer);
        }

        if (parentId is not null)
        {
            AddText("parentId", parentId);
        }

        if (fileBytes is not null)
        {
            var file = new ByteArrayContent(fileBytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(fileContentType ?? "application/octet-stream");
            form.Add(file, "attachment", fileName ?? "upload.bin");
        }

        return await Client.PostAsync("/api/comments", form);
    }

    /// <summary>Creates a valid comment and asserts HTTP 201, returning the CommentDto node.</summary>
    protected async Task<JsonNode> CreateCommentAsync(
        string userName = "QaUser1",
        string? email = null,
        string? homePage = null,
        string? text = null,
        string? parentId = null,
        byte[]? fileBytes = null,
        string? fileName = null,
        string? fileContentType = null)
    {
        email ??= $"{userName.ToLowerInvariant()}@example.com";
        text ??= "Hello <i>world</i>";
        var response = await PostCommentRawAsync(
            userName: userName,
            email: email,
            homePage: homePage,
            text: text,
            parentId: parentId,
            fileBytes: fileBytes,
            fileName: fileName,
            fileContentType: fileContentType);

        if (response.StatusCode != HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected 201 Created but got HTTP {(int)response.StatusCode}: {body}");
        }

        var node = await ReadJsonAsync(response);
        return UnwrapComment(node);
    }

    // ------------------------------------------------------------------- GET

    protected async Task<JsonNode> ListAsync(int? page = null, int? pageSize = null, string? sortBy = null, string? sortDir = null)
    {
        var query = new List<string>();
        if (page is not null)
        {
            query.Add($"page={page}");
        }

        if (pageSize is not null)
        {
            query.Add($"pageSize={pageSize}");
        }

        if (sortBy is not null)
        {
            query.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
        }

        if (sortDir is not null)
        {
            query.Add($"sortDir={Uri.EscapeDataString(sortDir)}");
        }

        var url = "/api/comments" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        var response = await Client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> HTTP {(int)response.StatusCode}");
        return await ReadJsonAsync(response);
    }

    protected async Task<HttpResponseMessage> PreviewAsync(string text)
        => await Client.PostAsync(
            "/api/preview",
            new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json"));

    // ------------------------------------------------------------ assertions

    protected static async Task<JsonNode> ExpectValidationErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var node = await ReadJsonAsync(response);
        Assert.Equal(400, node["status"]?.GetValue<int>());
        return node["errors"]
               ?? throw new Xunit.Sdk.XunitException("400 response is missing the 'errors' object.");
    }

    protected static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> isDone, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var last = default(T);
        while (sw.Elapsed < timeout)
        {
            last = await read();
            if (isDone(last))
            {
                return last;
            }

            await Task.Delay(100);
        }

        return last!;
    }
}
