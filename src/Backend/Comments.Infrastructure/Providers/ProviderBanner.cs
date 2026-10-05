using Comments.Application.Abstractions.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Comments.Infrastructure.Providers;

/// <summary>
/// Logs the resolved provider set once at startup (docs/ARCHITECTURE-v2.md §3), so "what are we
/// actually running on" is answerable from the container log alone — no need to exec in and
/// re-read configuration, and no chance of the log disagreeing with the adapters actually wired.
/// </summary>
internal sealed class ProviderBanner : IHostedService
{
    private readonly ActiveProviders _providers;
    private readonly ILogger<ProviderBanner> _logger;

    public ProviderBanner(ActiveProviders providers, ILogger<ProviderBanner> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Providers: {Selection} (strict={Strict}, failFastOnUnavailable={FailFast})",
            _providers.Describe(),
            _providers.Strict,
            _providers.FailFastOnUnavailable);

        foreach (var warning in _providers.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
