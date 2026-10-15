using TicTacToeUltimate.Server.Abstractions;

namespace TicTacToeUltimate.Server.Analysis;

/// <summary>Registers the tablebase cache (hosted, warms in the background) and the analysis service.</summary>
public static class AnalysisServiceCollectionExtensions
{
    /// <summary>Adds <see cref="TablebaseCache"/> and <see cref="AnalysisService"/>.</summary>
    public static IServiceCollection AddTttAnalysis(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<TablebaseCache>();
        services.AddSingleton<ITablebaseCache>(sp => sp.GetRequiredService<TablebaseCache>());
        services.AddHostedService(sp => sp.GetRequiredService<TablebaseCache>());
        services.AddSingleton<IAnalysisService, AnalysisService>();
        return services;
    }
}
