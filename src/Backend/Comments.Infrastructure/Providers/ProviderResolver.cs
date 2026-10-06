using Comments.Application.Abstractions.Providers;
using Microsoft.Extensions.Configuration;

namespace Comments.Infrastructure.Providers;

/// <summary>
/// Turns configuration into an <see cref="ActiveProviders"/> value (docs/ARCHITECTURE-v2.md §3).
/// This is the only place that reads the provider keys and validates them. An invalid value fails
/// startup with a message listing what is allowed — a typo must never silently pick a different
/// adapter — and there is exactly one key per port to get right.
///
/// <code>
/// Providers:Database              (default postgres)
/// Providers:Cache                 (default redis)
/// Providers:Messaging             (default rabbitmq)
/// Providers:Search                (default elastic)
/// Providers:Storage               (default filesystem)
/// Providers:Strict                (default true)   unknown/unimplemented value = startup error
/// Providers:FailFastOnUnavailable (default false)  selected-but-unreachable = startup error
/// </code>
/// </summary>
public static class ProviderResolver
{
    public static ActiveProviders Resolve(IConfiguration configuration)
    {
        var strict = Flag(configuration, "Providers:Strict", fallback: true);
        var failFast = Flag(configuration, "Providers:FailFastOnUnavailable", fallback: false);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var port in ProviderCatalog.Ports)
        {
            var configured = Raw(configuration, port)?.Trim();
            var value = string.IsNullOrEmpty(configured) ? ProviderCatalog.DefaultFor(port) : configured;
            var descriptor = ProviderCatalog.Find(port, value);

            if (descriptor is null)
            {
                var message =
                    $"Providers:{ProviderCatalog.Title(port)}='{value}' is not a known {port} provider. "
                    + $"Allowed values: {ProviderCatalog.AllValues(port)}.";
                if (strict)
                {
                    throw new InvalidOperationException(message);
                }

                warnings.Add(message + $" Falling back to '{ProviderCatalog.DefaultFor(port)}'.");
                value = ProviderCatalog.DefaultFor(port);
            }
            else if (!descriptor.Implemented)
            {
                var message =
                    $"Providers:{ProviderCatalog.Title(port)}='{value}' is a declared but unimplemented seam. "
                    + $"{descriptor.Description}. Implemented values: {ProviderCatalog.ImplementedValues(port)}.";
                if (strict)
                {
                    throw new InvalidOperationException(message);
                }

                warnings.Add(message + $" Falling back to '{ProviderCatalog.DefaultFor(port)}'.");
                value = ProviderCatalog.DefaultFor(port);
            }

            values[port] = value.ToLowerInvariant();
        }

        return new ActiveProviders(values, strict, failFast, warnings);
    }

    /// <summary>
    /// The canonical key for a port. There used to be a second, legacy set of keys
    /// (<c>Cache:Provider</c>, <c>Messaging:Provider</c>, <c>Storage:Provider</c>,
    /// <c>Search:Enabled</c>) honoured as aliases for compatibility with the previous iteration of
    /// this same project. It bought nothing for a single deliverable and cost a branch per port plus
    /// a paragraph in four documents, so the canonical key is now the only one.
    /// </summary>
    private static string? Raw(IConfiguration configuration, string port)
        => configuration[$"Providers:{ProviderCatalog.Title(port)}"];

    private static bool Flag(IConfiguration configuration, string key, bool fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : Flag(configuration[key]!, fallback);

    /// <summary>Lenient boolean parse: anything but an explicit no is "yes" (matches ASP.NET config).</summary>
    private static bool Flag(string raw, bool fallback)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return fallback;
        }

        return !(value.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("0", StringComparison.Ordinal)
                 || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }
}
