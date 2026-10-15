namespace TicTacToeUltimate.Server.Platform;

/// <summary>WP3 stub.</summary>
public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddTttPlatform(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env) => services;
    public static WebApplication MapTttPlatform(this WebApplication app) => app;
}
