using Comments.Application.Abstractions;
using Comments.Application.Services;
using Comments.Application.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Comments.Application;

/// <summary>
/// Composition entry point for the Application layer (docs/ARCHITECTURE-v2.md §4).
/// Registers pure services only; ports are wired by <c>AddCommentsInfrastructure</c>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCommentsApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<HtmlSanitizer>();

        services.AddScoped<ICommentQueryService, CommentQueryService>();
        services.AddScoped<ICommentCreateService, CommentCreateService>();

        return services;
    }
}
