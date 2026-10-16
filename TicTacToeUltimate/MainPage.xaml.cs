namespace TicTacToeUltimate;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
#if IOS || ANDROID
        // Banner during play and on menus (owner decision 2026-10-16). Test unit in Debug / without ids. Sits under the web view,
        // above the home indicator: the web view keeps its own safe-area handling.
        try
        {
            var banner = new Plugin.AdMob.BannerAd { HeightRequest = 50 };
            BannerHost.Content = banner;
            BannerHost.IsVisible = true;
            BannerHost.Padding = new Thickness(0, 0, 0, 0);
        }
        catch (Exception)
        {
            BannerHost.IsVisible = false;   // no AdMob (e.g. missing SDK init): the layout just has no banner row
        }
#endif
    }
}
