using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory with a per-instance temp SQLite database, temp storage root and the
/// Development-only CAPTCHA peek feature enabled. Each instance gets its own files so test
/// classes never share state.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    public string RootDir { get; }
    public string DbPath { get; }
    public string StorageDir { get; }

    public TestAppFactory()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "comments-qa", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDir);
        DbPath = Path.Combine(RootDir, "comments.db");
        StorageDir = Path.Combine(RootDir, "storage");
        Directory.CreateDirectory(StorageDir);

        // IMPORTANT: Program.cs reads GetConnectionString("Default") immediately after
        // WebApplication.CreateBuilder(args), i.e. BEFORE WebApplicationFactory's
        // ConfigureAppConfiguration callbacks run. Environment variables are read by
        // CreateBuilder itself, so they are the only reliable seam here. Test collections are
        // run sequentially (see AssemblyInfo.cs) so mutating the process environment is safe.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={DbPath}");
        Environment.SetEnvironmentVariable("Storage__Root", StorageDir);
        Environment.SetEnvironmentVariable("Features__DevCaptchaPeek", "true");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development enables the dev CAPTCHA peek endpoint (Features:DevCaptchaPeek=true).
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Both ":" and "__" spellings so either configuration style works.
                ["ConnectionStrings:Default"] = $"Data Source={DbPath}",
                ["ConnectionStrings__Default"] = $"Data Source={DbPath}",
                ["Features:DevCaptchaPeek"] = "true",
                ["Features__DevCaptchaPeek"] = "true",
                ["Storage:Root"] = StorageDir,
                ["Storage__Root"] = StorageDir,
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup of the temp dir; the OS temp folder is not part of the repo.
        }
    }
}
