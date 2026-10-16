#!/usr/bin/env bash
# Run Tic Tac Toe Ultimate on a real iPhone, or build a TestFlight archive.
#
#   scripts/ios-device.sh run        # Debug build, install + launch on the paired iPhone (needs a Development profile, see below)
#   scripts/ios-device.sh archive    # Release .ipa signed with the App Store profile (upload with Transporter → TestFlight)
#   scripts/ios-device.sh devices    # list paired devices / installed profiles
#
# One-time setup for `run` (about 3 minutes in the Apple Developer portal):
#   1. Register the iPhone: https://developer.apple.com/account/resources/devices/add  (UDID below, from `devices`).
#   2. Create an "iOS App Development" profile for the App ID com.wedirectsoftwares.tictactoeultimate, pick the
#      "Apple Development" certificate and the iPhone: https://developer.apple.com/account/resources/profiles/add
#   3. Download it and double-click it (Xcode installs it under ~/Library/Developer/Xcode/UserData/Provisioning Profiles).
#   Then `scripts/ios-device.sh run`. The script finds the profile by its app id.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJ=TicTacToeUltimate/TicTacToeUltimate.csproj
APPID=com.wedirectsoftwares.tictactoeultimate
DEV_KEY="${TTT_IOS_DEV_KEY:-$(security find-identity -v -p codesigning | grep -o '"Apple Development: [^"]*"' | head -1 | tr -d '"')}"
DIST_KEY="${TTT_IOS_CODESIGN_KEY:-$(security find-identity -v -p codesigning | grep -o '"Apple Distribution: [^"]*"' | head -1 | tr -d '"')}"
UDID="${TTT_IOS_UDID:-$(xcrun devicectl list devices 2>/dev/null | grep physical | grep -oE '[0-9A-F]{8}-[0-9A-F]{16}' | head -1)}"

profiles() {
  find ~/Library/MobileDevice/Provisioning\ Profiles ~/Library/Developer/Xcode/UserData/Provisioning\ Profiles -name '*.mobileprovision' 2>/dev/null
}
profile_named() { # $1 = "development" | "appstore"
  for f in $(profiles | tr ' ' '\001'); do
    f="${f//$'\001'/ }"
    local plist name app devices
    plist=$(security cms -D -i "$f" 2>/dev/null) || continue
    app=$(echo "$plist" | plutil -extract Entitlements.application-identifier raw - 2>/dev/null || true)
    [[ "$app" == *".$APPID" ]] || continue
    name=$(echo "$plist" | plutil -extract Name raw - 2>/dev/null)
    devices=$(echo "$plist" | plutil -extract ProvisionedDevices json -o - - 2>/dev/null || echo "")
    if [[ "$1" == development && -n "$devices" && "$devices" != "null" ]]; then echo "$name"; return; fi
    if [[ "$1" == appstore && ( -z "$devices" || "$devices" == "null" ) ]]; then echo "$name"; return; fi
  done
}

case "${1:-}" in
  devices)
    echo "Paired devices:"; xcrun devicectl list devices 2>/dev/null | grep -E "physical|Name" || true
    echo; echo "Profiles for $APPID:"
    for f in $(profiles | tr ' ' '\001'); do f="${f//$'\001'/ }"; security cms -D -i "$f" 2>/dev/null | plutil -extract Name raw - 2>/dev/null; done
    ;;
  run)
    [[ -n "$UDID" ]] || { echo "No paired iPhone found. Plug it in, unlock it and trust this Mac."; exit 1; }
    PROFILE=$(profile_named development)
    [[ -n "$PROFILE" ]] || { echo "No Development provisioning profile for $APPID. Follow the one-time setup at the top of this script (device UDID: $UDID)."; exit 1; }
    echo "Signing with: $DEV_KEY / $PROFILE → device $UDID"
    # Build first, then install + launch (the Run target alone cannot compute the mlaunch arguments).
    dotnet build "$PROJ" -f net10.0-ios -r ios-arm64 -c Debug -p:CodesignKey="$DEV_KEY" -p:CodesignProvision="$PROFILE"
    dotnet build "$PROJ" -f net10.0-ios -r ios-arm64 -c Debug -t:Run --no-restore \
      -p:CodesignKey="$DEV_KEY" -p:CodesignProvision="$PROFILE" -p:_DeviceName="$UDID"
    ;;
  archive)
    PROFILE=$(profile_named appstore)
    [[ -n "$PROFILE" ]] || { echo "No App Store provisioning profile for $APPID installed."; exit 1; }
    echo "Archiving with: $DIST_KEY / $PROFILE"
    TTT_IOS_CODESIGN_KEY="$DIST_KEY" TTT_IOS_PROVISIONING_PROFILE="$PROFILE" \
      dotnet publish "$PROJ" -f net10.0-ios -r ios-arm64 -c Release -p:ArchiveOnBuild=true -p:CodesignKey="$DIST_KEY" -p:CodesignProvision="$PROFILE"
    echo "The .ipa is under TicTacToeUltimate/bin/Release/net10.0-ios/ios-arm64/publish/. Upload it with Transporter (Mac App Store) to App Store Connect → TestFlight → add yourself as a tester."
    ;;
  *) sed -n 2,16p "$0"; exit 1 ;;
esac
