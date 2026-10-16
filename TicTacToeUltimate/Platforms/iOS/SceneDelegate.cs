using Foundation;

namespace TicTacToeUltimate;

/// <summary>
/// UIScene lifecycle (required by the iOS 26 SDK: UIKit traps at launch when an app has no scene manifest,
/// "__UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption"). Referenced from Info.plist UIApplicationSceneManifest.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
