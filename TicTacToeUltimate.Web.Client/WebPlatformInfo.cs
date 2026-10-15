using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Web.Client;

/// <summary>Browser platform info. Haptics are delegated to the JS vibrate API by Shared.</summary>
public sealed class WebPlatformInfo(ClientOptions options) : IPlatformInfo
{
    /// <inheritdoc />
    public ClientPlatform Platform => ClientPlatform.Web;
    /// <inheritdoc />
    public bool IsTouch => false;
    /// <inheritdoc />
    public bool SupportsHaptics => false;
    /// <inheritdoc />
    public string AppVersion => options.ClientVersion;
}
