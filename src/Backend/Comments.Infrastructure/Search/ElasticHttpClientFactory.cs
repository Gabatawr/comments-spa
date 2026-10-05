using System.Net.Http.Headers;
using System.Text;

namespace Comments.Infrastructure.Search;

/// <summary>
/// Builds the <see cref="HttpClient"/> used by <see cref="ElasticsearchSearchIndex"/>
/// (docs/API-v2.md §8.3). Kept separate from the composition root so the auth rules live next to
/// the adapter that depends on them.
/// </summary>
internal static class ElasticHttpClientFactory
{
    public static HttpClient Create(ElasticOptions options)
    {
        var handler = new HttpClientHandler();

        if (options.AllowInvalidCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300)),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", options.ApiKey.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = $"{options.Username}:{options.Password}";
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        return client;
    }
}
