# Bot protocol v1

Normative description of the bot WebSocket gateway. Source of truth for the shapes: `TicTacToeUltimate.Contracts.Bot` (`BotServerMessage`, `BotClientMessage`), serialized with `ContractsJsonContext`.

## Transport

- Endpoint: `GET /bot/ws?protocol=1` (WebSocket upgrade).
- Auth: header `Authorization: Bearer tttb_{keyId}_{secret}`. `?key=tttb_…` is accepted only when the server sets `Bots:AllowQueryKey`.
  The server looks the key up by `keyId` and compares `SHA256(secret)` in constant time. Keys are created with `POST /api/bots/keys` (shown once).
- UTF-8 JSON **text** frames, one message per frame, camelCase property names, enums as strings (`"CasualX2"`, `"AwaitingPlacement"`, `"X"`). Null properties are omitted.
- Every message has a `"type"` discriminator. Client requests may carry `"id"`; the server echoes it in `ack` / `error`.
- Max inbound message: 64 KiB.

## Close codes

| Code | Meaning |
|------|---------|
| 4401 | Missing, unknown, revoked or wrong key |
| 4426 | `protocol` query missing or not `1` |
| 4408 | 3 pings in a row were not answered with `pong` (ping every 15 s) |
| 1009 | Message too big |

## Sequence

1. Connect with the key and `?protocol=1`.
2. Server sends `hello`.
3. Bot sends `seek` (or `acceptChallenge`). Server replies `ack` when the request has an `id`.
4. For each match: server sends `gameStart`, then `state`.
5. Bot sends `action` when `yourTurn` is true.
6. Server sends `ack` or `error` for the action, **then** `state` after every applied action (both sides).
7. Server sends `state` (phase `Ended`) and then `gameEnd`.

One socket multiplexes up to `maxConcurrentGames` matches; every match message carries `matchId`.
`ply` in `action` is the expected ply; a stale value gives `error` code `stale_ply`.
Actions are idempotent per connection: the server derives the action id from `(session, id)`, so resending the same `id` on the same socket is safe.

## Server → bot

```jsonc
{"type":"hello","protocol":1,"botId":"…","botName":"dev_bot","serverTimeMs":1760500000000}
{"type":"gameStart","matchId":"…","mode":"Casual","timeControl":"CasualX2","rules":{"id":"n3k3-fifo7-v1-w0-none","boardSize":3,"winLength":3,"removal":"GlobalFifo","window":7,"vacatedCellPlayable":true,"vanishingPieceCanWin":false,"opening":"None"},"seat":1,"you":"O","opponent":{"userId":"…","displayName":"ali_xo","avatarId":"a1","kind":"Account","provisional":false}}
{"type":"state","matchId":"…","seq":9,"ply":7,"phase":"AwaitingPlacement","toMove":"O","you":"O","yourTurn":true,"board":"O.OOXXX.X","actions":"4,0,8,2,6,3,5","nextToVanish":4,"legal":[1,4,7],"swapped":false,"deadlineAtMs":1760500031000,"allowanceMs":30000,"serverTimeMs":1760500001000}
{"type":"ack","id":"m-17","matchId":"…","seq":10}
{"type":"error","id":"m-17","matchId":"…","code":"occupied","message":"Cell 4 is occupied"}
{"type":"gameEnd","matchId":"…","outcome":"OWins","reason":"Line","winnerSeat":1,"winLine":[0,1,2],"youWon":true}
{"type":"ping","t":123}
```

| type | fields |
|------|--------|
| `hello` | `protocol` int, `botId` uuid, `botName`, `serverTimeMs` |
| `gameStart` | `matchId`, `mode` (MatchMode), `timeControl` (TimeControl), `rules` (RuleSetDto), `seat` (0 = starts as X, 1), `you` (Color), `opponent` (PlayerCardDto) |
| `state` | `matchId`, `seq`, `ply`, `phase` (GamePhase), `toMove`, `you`, `yourTurn`, `board` (N² chars `X`/`O`/`.` row-major), `actions` (notation: cells, `swap`, `keep`, comma-joined), `nextToVanish`?, `legal` int[], `swapped`, `deadlineAtMs`?, `allowanceMs`?, `serverTimeMs` |
| `gameEnd` | `matchId`, `outcome` (Outcome), `reason` (EndReason), `winnerSeat`?, `winLine` int[], `youWon` |
| `ack` | `id`, `matchId`?, `seq`? |
| `error` | `id`?, `matchId`?, `code` (ErrorCodes), `message` |
| `ping` | `t` long — reply with `pong` carrying the same `t` |

`you` can change after a pie `swap` (seat stays, colour flips).

## Bot → server

```jsonc
{"type":"seek","id":"s1","timeControls":["CasualX2","Unlimited"],"maxConcurrentGames":4}
{"type":"unseek","id":"s2"}
{"type":"acceptChallenge","id":"c1","code":"K7Q2XM4P"}
{"type":"action","id":"m-17","matchId":"…","ply":7,"kind":"Place","cell":1}
{"type":"action","id":"m-18","matchId":"…","ply":1,"kind":"Swap"}
{"type":"resign","matchId":"…"}
{"type":"getState","matchId":"…"}
{"type":"pong","t":123}
```

| type | fields |
|------|--------|
| `seek` | `id`?, `timeControls` TimeControl[] (non-empty, live controls only — not `Daily`), `maxConcurrentGames` ≥ 1. Replaces any previous seek. |
| `unseek` | `id`? — stop receiving new matches; running matches continue |
| `acceptChallenge` | `id`, `code` — `ack` carries the new `matchId`, then `gameStart` |
| `action` | `id`, `matchId`, `ply` (expected), `kind` (`Place`/`Swap`/`KeepSides`), `cell` (required for `Place`) |
| `resign` | `id`?, `matchId` |
| `getState` | `id`?, `matchId` — `ack` then a fresh `state` |
| `pong` | `t` |

## Error codes

`error.code` uses the shared `ErrorCodes` catalogue: `stale_ply`, `not_your_turn`, `occupied`, `cell_just_vacated`, `wrong_action_kind`, `illegal_move`, `banned_opening`, `match_over`, `match_not_found`, `not_a_participant`, `challenge_not_found`, `challenge_expired`, `challenge_taken`, `validation` (malformed or unknown message), `internal`.

## Matchmaking

A seeking bot is offered to human queue entries that waited `Matchmaking:BotFallbackSeconds` (20 s) in a time control the bot seeks, while it has free capacity. Bot matches are never Ranked unless the server sets `Bots:Ranked`.
