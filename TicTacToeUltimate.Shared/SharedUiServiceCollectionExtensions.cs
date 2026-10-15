using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Shared.Platform;
using TicTacToeUltimate.Shared.Services;

namespace TicTacToeUltimate.Shared;

/// <summary>Registers the shared UI and its JS-backed platform defaults (§8.4).</summary>
public static class SharedUiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TttJsModule"/>, the Js* platform defaults, <see cref="AdOverlayService"/> (as <see cref="IAdOverlay"/>)
    /// and <see cref="ToastService"/>. Everything uses TryAdd*, so hosts may override afterwards (last registration wins).
    /// </summary>
    public static IServiceCollection AddTttSharedUi(this IServiceCollection services)
    {
        services.TryAddSingleton<TttJsModule>();
        services.TryAddSingleton<IKeyValueStore, JsKeyValueStore>();
        services.TryAddSingleton<ISecureStore, JsSecureStore>();
        services.TryAddSingleton<IHaptics, JsHaptics>();
        services.TryAddSingleton<ISoundService, JsSoundService>();
        services.TryAddSingleton<IShareService, JsShareService>();
        services.TryAddSingleton<IAppLifecycle, JsLifecycle>();
        services.TryAddSingleton<IDeepLinkSource, NoopDeepLinkSource>();
        services.TryAddSingleton<IPushService, NoopPushService>();
        services.TryAddSingleton<IPlatformInfo, DefaultPlatformInfo>();
        services.TryAddSingleton<AdOverlayService>();
        services.TryAddSingleton<IAdOverlay>(sp => sp.GetRequiredService<AdOverlayService>());
        services.TryAddSingleton<ToastService>();
        return services;
    }
}
