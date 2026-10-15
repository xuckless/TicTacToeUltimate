TTT Ultimate is a fast, competitive tic-tac-toe where pieces vanish, so every game ends in a win. The look pairs chess.com's calm, focused match screen with Duolingo's chunky, animated delight. It is dark-first and purple.

## Voice and copy

- Speak to the player as "you". Write short, energetic sentences: "Your move", "3 in a row!", "Streak saved".
- Use sentence case in body copy. Use UPPERCASE `label` only on buttons, tabs and chips ("PLAY RANKED", "SUDDEN DEATH").
- Never use emoji as decoration. Emotes are a product feature and appear only inside the emote picker and emote bubbles.
- Write numbers as numerals: "+12", "0:09", "Win 3 games".

## Color

- Paint every screen `bg`. Cards and player cards sit on `surface`. Board cells, inputs and chips sit on `surface-raised`.
- Purple is the brand:
  - `primary-fill` + `on-primary` for the one main action per screen (Play, Find match, Accept).
  - `primary` for purple text, active tabs and selected outlines.
  - Never place two primary-fill buttons side by side.
- X is always `piece-x` (cyan) and O is always `piece-o` (orange). The hues differ in lightness as well as hue, and the glyphs differ too. Never swap them for cosmetics without keeping that contrast.
- Red is reserved for tension. `alert` / `alert-fill` appear only in sudden death, at 3s or less on any clock, on errors and on Resign. This keeps the red-alert moment shocking.
- `streak` (gold) is only for the streak flame, quest rewards and the dark focus ring.
- `success` always pairs with a sign or check glyph (+12, ✓). It never carries meaning by color alone.
- Tier colors (`tier-bronze` … `tier-master`) appear only on tier badges and rank text.
- `line` is decorative. Control borders and the board grid use `line-strong`.

## Type

- `Lilita One` (`display`) is the game voice: the `hero`, `title` and `heading` styles. Use it sparingly: result banners, screen titles, the Play hero.
- `Nunito` (`sans`) at weight 600–800 for all UI text: `body`, `body-sm`, `label`.
- `Chakra Petch` (`numeric`) for clocks and ratings (`clock`, `rating`), always with `font-variant-numeric: tabular-nums` so digits don't jitter while counting down.

## Shape, depth, spacing

- Everything tappable is chunky. Primary buttons get a 4px ledge (`ledge-primary`). Secondary buttons and board cells get `ledge-neutral`. On press the element moves down 4px and its ledge collapses to 0.
- Radii: `radius-md` for buttons and cells, `radius-lg` for cards and the board frame, `radius-pill` for clock and streak pills and avatars.
- Use `space-4` as the screen gutter, `space-6` between sections and `space-8` around the board.

## The board

- 3x3 cells on `surface-raised` inside a `radius-lg` frame on `surface`, with `space-2` gaps.
- The piece that will vanish next is drawn at 35% opacity with a dashed outline in its own piece color. It shows only once the window is full.
- A win draws a thick line in the winner's piece color across the three cells, then triggers the impact frame.

## Motion and impact frames

- **Place:** the piece scales from 0.6 to 1.08 to 1 in 180ms, with a soft thud.
- **Vanish:** the faded piece shrinks and dissolves in 220ms, then the new piece lands.
- **Clock states:**
  - Normal: ring in the player's piece color.
  - Warning (5s or less): the ring turns `alert`.
  - Sudden death: the clock pill fills `alert-fill`, pulses with `glow-alert` once per second, and the heartbeat sound speeds up as the move window shrinks.
- **Impact frame:** used on a win, on the sudden-death start and on a promotion.
  1. Freeze for 2 frames on a full-screen flash: `alert-fill` for sudden death, the winner's piece color for a win, the tier color for a promotion.
  2. Show a high-contrast silhouette frame.
  3. Slam in the `hero` text (scale 1.4 → 1) with a 6px screen shake for 120ms.
- Under `prefers-reduced-motion`, replace the shake, flash and pulse with a static color change. Haptics and sound remain.

## Layout

- **Mobile:** a 4-tab bottom nav (Home, Play, Social, Profile). The active tab is `primary`, labels use `label`, and the nav sits on `surface` above the safe area.
- **Desktop web:** the same four destinations as a left sidebar.
- **Match screen:**
  - Opponent's player card at the top, yours at the bottom, board centered between them.
  - The emote, resign and menu buttons sit in a row under your card.
  - Ads never appear on the match screen.

## Focus and accessibility

- Every focusable control shows a 2px solid `focus` ring with a 2px offset.
- Text pairs meet 4.5:1 in both themes. Board pieces and control borders meet 3:1.
