using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Comments.Application.Abstractions.Search;
using Comments.Application.Dtos;
using Comments.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Comments.Infrastructure.Search;

/// <summary>
/// Thin Elasticsearch 8 REST adapter behind <see cref="ICommentSearchIndex"/> (docs/API-v2.md §8.3).
/// Uses a dedicated <see cref="HttpClient"/> so there is no client-library version drift; endpoints:
/// <c>PUT /{index}</c>, <c>PUT /{index}/_doc/{id}</c>, <c>POST /{index}/_search</c> and <c>GET /</c>.
/// </summary>
public sealed class ElasticsearchSearchIndex : ICommentSearchIndex
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ElasticOptions _options;
    private readonly ILogger<ElasticsearchSearchIndex> _logger;

    private readonly object _probeLock = new();
    private bool _lastProbeResult;
    private DateTime _lastProbeAt = DateTime.MinValue;

    public ElasticsearchSearchIndex(
        HttpClient http,
        IOptions<ElasticOptions> options,
        ILogger<ElasticsearchSearchIndex> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.Url.TrimEnd('/') + "/");
        }

        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300));
    }

    /// <summary>
    /// Always true. This adapter is constructed only when <c>Providers:Search=elastic</c>, and
    /// "search is off" is a different adapter (<see cref="NoopSearchIndex"/>) rather than a flag
    /// inside this one — a flag here would be a second switch for a decision the container has
    /// already made, and one that can disagree with what <c>/api/info</c> reports.
    /// </summary>
    public bool IsEnabled => true;

    public bool IsAvailable
    {
        get
        {
            lock (_probeLock)
            {
                if (DateTime.UtcNow - _lastProbeAt < TimeSpan.FromSeconds(5))
                {
                    return _lastProbeResult;
                }

                try
                {
                    using var response = _http.Send(new HttpRequestMessage(HttpMethod.Get, string.Empty));
                    _lastProbeResult = response.IsSuccessStatusCode;
                }
                catch
                {
                    _lastProbeResult = false;
                }

                _lastProbeAt = DateTime.UtcNow;
                return _lastProbeResult;
            }
        }
    }

    public async Task EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var mapping = BuildMappingJson();
            using var content = new StringContent(mapping, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PutAsync(IndexPath, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Elasticsearch index {Index} created", _options.IndexName);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("resource_already_exists_exception", StringComparison.Ordinal))
            {
                return; // idempotent
            }

            _logger.LogWarning("Elasticsearch index creation returned {Status}: {Body}", (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            // Search is a non-critical dependency: never crash startup (docs/ARCHITECTURE-v2.md §5).
            _logger.LogWarning(ex, "Elasticsearch EnsureIndex failed; search will report error");
        }
    }

    public async Task IndexAsync(CommentDto comment, CancellationToken cancellationToken = default)
    {
        var document = new JsonObject
        {
            ["id"] = comment.Id,
            ["parentId"] = comment.ParentId,
            ["userName"] = comment.UserName,
            ["email"] = comment.Email,
            ["textPlain"] = comment.TextPlain,
            ["createdAt"] = CommentMapper.AsUtc(comment.CreatedAt).ToString("O", CultureInfo.InvariantCulture),
            ["clientIp"] = NormalizeIp(comment.ClientIp),
        };

        using var content = new StringContent(document.ToJsonString(Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync($"{IndexPath}/_doc/{comment.Id}", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Elasticsearch indexing failed ({(int)response.StatusCode}): {body}");
        }
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["from"] = query.From,
            ["size"] = query.PageSize,

            // Without this, Elasticsearch 7.x/8.x caps hits.total.value at 10 000, so totalItems and
            // totalPages would stop counting past ten thousand documents — silently, and only once
            // the index is actually big enough to matter (docs/API-v2.md §3.1).
            ["track_total_hits"] = true,

            ["query"] = new JsonObject
            {
                ["multi_match"] = new JsonObject
                {
                    ["query"] = query.Query,
                    ["fields"] = new JsonArray("textPlain^2", "userName", "email"),
                },
            },
            ["highlight"] = new JsonObject
            {
                ["fields"] = new JsonObject { ["textPlain"] = new JsonObject() },
            },
        };

        if (query.Sort == "createdAt")
        {
            request["sort"] = new JsonArray(new JsonObject
            {
                ["createdAt"] = new JsonObject { ["order"] = query.Ascending ? "asc" : "desc" },
            });
        }

        using var content = new StringContent(request.ToJsonString(Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{IndexPath}/_search", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Elasticsearch search failed ({(int)response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var took = root.TryGetProperty("took", out var tookElement) ? tookElement.GetInt64() : 0;

        long total = 0;
        if (root.TryGetProperty("hits", out var hitsElement))
        {
            if (hitsElement.TryGetProperty("total", out var totalElement))
            {
                total = totalElement.ValueKind == JsonValueKind.Object
                    ? totalElement.GetProperty("value").GetInt64()
                    : totalElement.GetInt64();
            }
        }

        var items = new List<SearchHit>();
        if (root.TryGetProperty("hits", out var hits)
            && hits.TryGetProperty("hits", out var hitArray)
            && hitArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hitArray.EnumerateArray())
            {
                if (!hit.TryGetProperty("_source", out var source))
                {
                    continue;
                }

                var dto = new CommentDto
                {
                    Id = GetInt(source, "id"),
                    ParentId = GetNullableInt(source, "parentId"),
                    UserName = GetString(source, "userName") ?? string.Empty,
                    Email = GetString(source, "email") ?? string.Empty,
                    TextPlain = GetString(source, "textPlain") ?? string.Empty,
                    CreatedAt = GetDate(source, "createdAt"),
                    ClientIp = GetString(source, "clientIp"),
                };

                items.Add(new SearchHit(dto, GetScore(hit), GetHighlight(hit)));
            }
        }

        var totalInt = (int)Math.Min(int.MaxValue, total);
        var totalPages = totalInt == 0 ? 0 : (int)Math.Ceiling(totalInt / (double)query.PageSize);
        return new SearchResult(items, query.Query, took, query.Page, query.PageSize, totalInt, totalPages);
    }

    private string IndexPath => Uri.EscapeDataString(_options.IndexName);

    private static string BuildMappingJson() => """
    {
      "mappings": {
        "properties": {
          "id":        { "type": "long" },
          "parentId":  { "type": "long" },
          "userName":  { "type": "keyword" },
          "email":     { "type": "keyword" },
          "textPlain": { "type": "text", "analyzer": "standard" },
          "createdAt": { "type": "date" },
          "clientIp":  { "type": "ip", "ignore_malformed": true }
        }
      }
    }
    """;

    private static double GetScore(JsonElement hit)
        => hit.TryGetProperty("_score", out var score) && score.ValueKind == JsonValueKind.Number
            ? score.GetDouble()
            : 0d;

    private static string? GetHighlight(JsonElement hit)
    {
        if (hit.TryGetProperty("highlight", out var highlight)
            && highlight.TryGetProperty("textPlain", out var fragments)
            && fragments.ValueKind == JsonValueKind.Array
            && fragments.GetArrayLength() > 0)
        {
            return fragments[0].GetString();
        }

        return null;
    }

    private static int GetInt(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static int? GetNullableInt(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string? GetString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime GetDate(JsonElement source, string name)
    {
        if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return CommentMapper.AsUtc(parsed);
        }

        return DateTime.UnixEpoch;
    }

    /// <summary>Malformed IPs must not break indexing: fall back to null (mapping has ignore_malformed).</summary>
    private static string? NormalizeIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return System.Net.IPAddress.TryParse(value.Trim(), out _) ? value.Trim() : null;
    }
}

/// <summary>
/// Search adapter used when <c>Providers:Search=none</c>: every operation is a no-op and the API
/// reports <c>health.search=disabled</c> / HTTP 503 for <c>/api/search</c>.
/// </summary>
public sealed class NoopSearchIndex : ICommentSearchIndex
{
    public bool IsEnabled => false;

    public bool IsAvailable => false;

    public Task EnsureIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task IndexAsync(CommentDto comment, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(new SearchResult(Array.Empty<SearchHit>(), query.Query, 0, query.Page, query.PageSize, 0, 0));
}
