using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Client.Abstractions;

namespace TicTacToeUltimate.Client;

/// <summary>WP5 stub: registers the client core (stores, realtime, REST).</summary>
public static class ClientServiceCollectionExtensions
{
    /// <summary>Registers client services.</summary>
    public static IServiceCollection AddTttClient(this IServiceCollection services, ClientOptions options)
    {
        services.AddSingleton(options);
        return services;
    }
}
