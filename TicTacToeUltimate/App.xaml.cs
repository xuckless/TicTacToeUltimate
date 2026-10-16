using TicTacToeUltimate.Services;

namespace TicTacToeUltimate;

public partial class App : Application
{
    private readonly MauiLifecycle _lifecycle;

    public App(MauiLifecycle lifecycle)
    {
        _lifecycle = lifecycle;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "TTT Ultimate" };
        _lifecycle.Attach(window);
#if IOS || ANDROID
        // Preload ads a few seconds after launch so the first break never waits on the network.
        window.Created += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            IPlatformApplication.Current?.Services.GetService<AdMobAdProvider>()?.Preload();
        };
#endif
        return window;
    }

    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        base.OnAppLinkRequestReceived(uri);
        MauiDeepLinkSource.Current?.Publish(uri.ToString());
    }
}
