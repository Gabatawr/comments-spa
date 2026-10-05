using Comments.Application.Abstractions.Providers;
using Microsoft.Extensions.Configuration;

namespace Comments.Infrastructure.Providers;

/// <summary>
/// Turns configuration into an <see cref="ActiveProviders"/> value (docs/ARCHITECTURE-v2.md §3).
/// This is the only place that reads the provider keys, validates them, and applies the legacy
/// aliases the v2.0 stack used. An invalid value fails startup with a message that lists what is
/// allowed — a typo must never silently pick a different adapter.
///
/// <code>
/// Providers:Database    (default postgres)      legacy: —
/// Providers:Cache       (default redis)         legacy: Cache:Provider
/// Providers:Messaging   (default rabbitmq)      legacy: Messaging:Provider
/// Providers:Search      (default elastic)       legacy: Search:Enabled=false → none
/// Providers:Storage     (default filesystem)    legacy: Storage:Provider
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

    private static string? Raw(IConfiguration configuration, string port) => port switch
    {
        ProviderCatalog.Database => configuration["Providers:Database"],
        ProviderCatalog.Cache => configuration["Providers:Cache"] ?? configuration["Cache:Provider"],
        ProviderCatalog.Messaging => configuration["Providers:Messaging"] ?? configuration["Messaging:Provider"],
        ProviderCatalog.Search => configuration["Providers:Search"] ?? SearchFromLegacyFlag(configuration),
        ProviderCatalog.Storage => configuration["Providers:Storage"] ?? configuration["Storage:Provider"],
        _ => null,
    };

    /// <summary>v2.0 spelled the search switch as a boolean flag; keep honouring it.</summary>
    private static string? SearchFromLegacyFlag(IConfiguration configuration) =>
        configuration["Search:Enabled"] is { } enabled
            ? (Flag(enabled, fallback: true) ? "elastic" : "none")
            : null;

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
