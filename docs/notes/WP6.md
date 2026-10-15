# WP6 notes (UI)

- **Tokens**: `wwwroot/css/tokens.css` is generated from `design/system/tokens.json` (dark on `:root`, light on `[data-theme=light]`, OS preference on `[data-theme=system]`). A test checks every token in both themes.
- **CSS entry points**: `css/{tokens,components,app,motion}.css`. `wwwroot/app.css` `@import`s all four, so hosts that still link `_content/TicTacToeUltimate.Shared/app.css` keep working.
- **Fonts**: no `.woff2` files are bundled (there was no network access to fetch the OFL fonts). The token fallback stacks are used. TODO: drop Lilita One, Nunito and Chakra Petch into `wwwroot/fonts` and add `@font-face`.
- **JS**: `TttJsModule` lazily imports `js/ttt.js`. `Routes` attaches the renderer's `IJSRuntime`, which handles the scoped runtime in Blazor Hybrid; before that it falls back to the root provider. All calls swallow JS failures. Audio unlocks itself on the first pointer or key event.
- **Rendering never calls the Engine** (WP1 stubs throw). The board comes from `Snapshot.Board`, the ghost from `Snapshot.NextToVanish` (only while the game runs), and the win line from `Result.WinLine`. `Board` refuses to draw a win line that includes the ghost cell. Replay rebuilds positions from `ActionDto` via `ReplayFrames`.
- **Red alert**: `.match.sd` plus `sd-1` (window ≥ 8 s), `sd-2` (5–7 s), `sd-3` (≤ 4 s) and `crit` (your clock is Critical). Pulse speed and glow escalate. Impact frames fire on sudden-death start, win, loss and promotion (result page), gated by `Settings.ImpactFrames`. Reduced motion uses `data-motion` (set by JS from the setting or the OS) and turns off all animation.
- **Ads**: `MatchLayout` cascades `AdsAllowed=false` and calls `IAdCoordinator.SetMatchRouteActive`. `AdOverlayHost` (in `Routes`) renders the `AdOverlayService` stub with a countdown. Closing early returns false.
- **Fakes**: `Fakes/` has fake stores plus `MockupFixtures` (mockup data, fixture ids per §6.7) and `AddTttFakeStores()`.
- **Deviation**: `BoardCell` is rendered inline inside `Board` rather than as its own component.
- **Host note (WP5)**: `AddTttSharedUi` also `TryAdd`s a `DefaultPlatformInfo`. Hosts should link the four CSS files (or `app.css`).
