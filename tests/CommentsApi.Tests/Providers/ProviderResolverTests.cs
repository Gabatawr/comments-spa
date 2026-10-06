using Xunit;
using Comments.Application.Abstractions.Providers;
using Comments.Application.Abstractions.Storage;
using Comments.Infrastructure;
using Comments.Infrastructure.Providers;
using Comments.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CommentsApi.Tests.Providers;

/// <summary>
/// The provider switchboard (docs/ARCHITECTURE-v2.md §3): defaults, the canonical
/// <c>Providers:*</c> keys, the legacy v2.0 aliases, and the fail-fast rules that keep a typo from
/// silently selecting a different adapter than the operator asked for.
/// </summary>
public class ProviderResolverTests
{
    private const string AzureConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=testacct;AccountKey="
        + "dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3RrZXk=;EndpointSuffix=core.windows.net";

    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal))
            .Build();

    [Fact]
    public void Defaults_match_the_local_compose_stack()
    {
        var providers = ProviderResolver.Resolve(Config());

        Assert.Equal("postgres", providers.Database);
        Assert.Equal("redis", providers.Cache);
        Assert.Equal("rabbitmq", providers.Messaging);
        Assert.Equal("elastic", providers.Search);
        Assert.Equal("filesystem", providers.Storage);
        Assert.True(providers.Strict);
        Assert.False(providers.FailFastOnUnavailable);
        Assert.Empty(providers.Warnings);
    }

    [Fact]
    public void Canonical_keys_drive_every_port()
    {
        var providers = ProviderResolver.Resolve(Config(
            ("Providers:Database", "postgres"),
            ("Providers:Cache", "memory"),
            ("Providers:Messaging", "inmemory"),
            ("Providers:Search", "none"),
            ("Providers:Storage", "s3")));

        Assert.Equal("memory", providers.Cache);
        Assert.Equal("inmemory", providers.Messaging);
        Assert.Equal("none", providers.Search);
        Assert.Equal("s3", providers.Storage);
        Assert.Equal(
            "database=postgres cache=memory messaging=inmemory search=none storage=s3",
            providers.Describe());
    }

    [Fact]
    public void Retired_v2_keys_are_ignored()
    {
        // Cache:Provider / Messaging:Provider / Storage:Provider / Search:Enabled were honoured as
        // aliases for compatibility with the previous iteration of this project. They are gone on
        // purpose: one canonical key per port is the whole point of the switchboard, and a silent
        // alias is a second way to configure the same decision. A deployment still setting them now
        // gets the documented default instead of a surprise.
        var providers = ProviderResolver.Resolve(Config(
            ("Cache:Provider", "memory"),
            ("Messaging:Provider", "inmemory"),
            ("Storage:Provider", "s3"),
            ("Search:Enabled", "false")));

        Assert.Equal("redis", providers.Cache);
        Assert.Equal("rabbitmq", providers.Messaging);
        Assert.Equal("filesystem", providers.Storage);
        Assert.Equal("elastic", providers.Search);
    }

    [Fact]
    public void Values_are_case_insensitive()
    {
        var providers = ProviderResolver.Resolve(Config(("Providers:Storage", "  S3 ")));

        Assert.Equal("s3", providers.Storage);
    }

    [Theory]
    [InlineData("Providers:Cache")]
    [InlineData("Providers:Messaging")]
    [InlineData("Providers:Search")]
    [InlineData("Providers:Storage")]
    [InlineData("Providers:Database")]
    public void Unknown_provider_aborts_startup_and_lists_the_allowed_values(string key)
    {
        var port = key.Split(':')[1].ToLowerInvariant();

        var error = Assert.Throws<InvalidOperationException>(() =>
            ProviderResolver.Resolve(Config((key, "definitely-not-a-provider"))));

        Assert.Contains("not a known", error.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderCatalog.AllValues(port), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_but_unimplemented_seam_aborts_startup_with_a_precise_message()
    {
        // mssql is in the catalog because the assignment prefers MS SQL, but shipping a second
        // migration set is out of scope — selecting it must say exactly that, not "unknown value".
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProviderResolver.Resolve(Config(("Providers:Database", "mssql"))));

        Assert.Contains("declared but unimplemented seam", error.Message, StringComparison.Ordinal);
        Assert.Contains("postgres", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_strict_mode_falls_back_and_records_a_warning()
    {
        var providers = ProviderResolver.Resolve(Config(
            ("Providers:Strict", "false"),
            ("Providers:Storage", "swift")));

        Assert.Equal("filesystem", providers.Storage);
        Assert.Single(providers.Warnings);
        Assert.Contains("swift", providers.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Self_contained_providers_are_flagged_for_health_reporting()
    {
        var providers = ProviderResolver.Resolve(Config(
            ("Providers:Cache", "memory"),
            ("Providers:Messaging", "inmemory"),
            ("Providers:Search", "none")));

        Assert.True(providers.IsSelfContained(ProviderCatalog.Cache));
        Assert.True(providers.IsSelfContained(ProviderCatalog.Messaging));
        Assert.True(providers.IsSelfContained(ProviderCatalog.Search));
        Assert.False(providers.IsSelfContained(ProviderCatalog.Storage));
        Assert.False(providers.IsSelfContained(ProviderCatalog.Database));
    }

    [Fact]
    public void Fail_fast_flag_is_read_from_configuration()
    {
        Assert.True(ProviderResolver.Resolve(Config(("Providers:FailFastOnUnavailable", "true"))).FailFastOnUnavailable);
        Assert.False(ProviderResolver.Resolve(Config(("Providers:FailFastOnUnavailable", "0"))).FailFastOnUnavailable);
    }

    // ------------------------------------------------------------------ catalog invariants

    [Fact]
    public void Every_port_declares_a_default_that_is_implemented()
    {
        foreach (var port in ProviderCatalog.Ports)
        {
            var descriptor = ProviderCatalog.Find(port, ProviderCatalog.DefaultFor(port));

            Assert.NotNull(descriptor);
            Assert.True(descriptor!.Implemented, $"{port} default '{descriptor.Value}' is not implemented.");
        }
    }

    [Fact]
    public void Switchable_ports_offer_at_least_one_alternative()
    {
        // cache / messaging / search / storage are the ports a deployment really re-points at
        // managed cloud services, so each of them must have more than one implemented option.
        foreach (var port in new[]
                 {
                     ProviderCatalog.Cache, ProviderCatalog.Messaging,
                     ProviderCatalog.Search, ProviderCatalog.Storage,
                 })
        {
            var implemented = ProviderCatalog.For(port).Count(p => p.Implemented);

            Assert.True(implemented >= 2, $"{port} has only {implemented} implemented provider(s).");
        }
    }

    [Fact]
    public void Database_exposes_the_ms_sql_seam_without_implementing_it()
    {
        // The assignment prefers MS SQL. The name is declared so selecting it fails with a precise
        // message instead of "unknown value", but only PostgreSQL has a migration set.
        var mssql = ProviderCatalog.Find(ProviderCatalog.Database, "mssql");

        Assert.NotNull(mssql);
        Assert.False(mssql!.Implemented);
        Assert.Single(ProviderCatalog.For(ProviderCatalog.Database), p => p.Implemented);
    }

    // ------------------------------------------------------------------ the switch actually wires

    public static TheoryData<string, Type> StorageAdapters => new()
    {
        { "filesystem", typeof(FileSystemStorage) },
        { "s3", typeof(S3FileStorage) },
        { "azureblob", typeof(AzureBlobFileStorage) },
    };

    [Theory]
    [MemberData(nameof(StorageAdapters))]
    public void Storage_switch_registers_the_matching_adapter(string value, Type expected)
    {
        // Proves the switch changes the object graph, not just the reported value: this is the
        // whole point of the "swap the provider by configuration" claim.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddCommentsInfrastructure(Config(
            ("Providers:Storage", value),
            ("Providers:Cache", "memory"),
            ("Providers:Messaging", "inmemory"),
            ("Providers:Search", "none"),
            ("Storage:S3:Bucket", "comments-attachments"),
            ("Storage:AzureBlob:ConnectionString", AzureConnectionString),
            ("Storage:AzureBlob:Container", "comments-attachments")));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.IsType(expected, storage);
        Assert.Equal(value, storage.Provider);
    }

    [Fact]
    public void Selecting_s3_without_a_bucket_fails_with_an_actionable_message()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddCommentsInfrastructure(Config(
            ("Providers:Storage", "s3"),
            ("Providers:Cache", "memory"),
            ("Providers:Messaging", "inmemory"),
            ("Providers:Search", "none")));

        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFileStorage>());
        Assert.Contains("Storage:S3:Bucket", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unimplemented_provider_name_has_no_adapter_to_wire()
    {
        // Belt and braces: even if validation is bypassed, the composition root refuses to pick a
        // silent default for a name it does not implement.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddCommentsInfrastructure(Config(
            ("Providers:Storage", "glacier"),
            ("Providers:Strict", "false"),
            ("Providers:Cache", "memory"),
            ("Providers:Messaging", "inmemory"),
            ("Providers:Search", "none")));

        using var provider = services.BuildServiceProvider();

        // Non-strict downgraded the unknown value to the default, so filesystem is wired.
        Assert.IsType<FileSystemStorage>(provider.GetRequiredService<IFileStorage>());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Comments.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
