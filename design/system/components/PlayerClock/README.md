# PlayerClock

Player card with avatar, name, tier and rating, plus the move clock pill. The opponent's card goes at the top of the match screen and yours at the bottom.

- **Consumer provides:** name, tier, rating, piece (`x` or `o`, which colors the ring), the remaining time, and one clock state: `idle` (not their turn), normal, `warn` (5s or less) or `sd` (sudden death).
- In sudden death the pill fills `alert-fill` and pulses once per second. The meta line becomes "SUDDEN DEATH · Ns window" so the shrinking limit is readable.
- The server owns the clock; the client only renders the countdown from server timestamps.
