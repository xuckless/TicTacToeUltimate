using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;
using System.Text.Json.Serialization;

namespace TicTacToeUltimate.Contracts.Bot;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BotHello), "hello")] [JsonDerivedType(typeof(BotGameStart), "gameStart")]
[JsonDerivedType(typeof(BotState), "state")] [JsonDerivedType(typeof(BotGameEnd), "gameEnd")]
[JsonDerivedType(typeof(BotAck), "ack")] [JsonDerivedType(typeof(BotError), "error")] [JsonDerivedType(typeof(BotPing), "ping")]
[JsonDerivedType(typeof(BotOpponentEmote), "opponentEmote")]
public abstract record BotServerMessage;
/// <summary>The opponent sent an emote (bots may answer with <see cref="BotEmote"/>).</summary>
public sealed record BotOpponentEmote(Guid MatchId, int Seat, string EmoteId) : BotServerMessage;
public sealed record BotHello(int Protocol, Guid BotId, string BotName, long ServerTimeMs) : BotServerMessage;
/// <summary>OpponentShadow: the opponent is in the anti-cheat shadow pool; play at full strength (bots/ brain shadow profile).</summary>
public sealed record BotGameStart(Guid MatchId, MatchMode Mode, TimeControl TimeControl, RuleSetDto Rules, int Seat, Color You,
    PlayerCardDto Opponent, bool OpponentShadow = false) : BotServerMessage;
public sealed record BotState(Guid MatchId, long Seq, int Ply, GamePhase Phase, Color ToMove, Color You, bool YourTurn,
    string Board, string Actions /* Notation */, int? NextToVanish, int[] Legal, bool Swapped,
    long? DeadlineAtMs, int? AllowanceMs, long ServerTimeMs) : BotServerMessage;
public sealed record BotGameEnd(Guid MatchId, Outcome Outcome, EndReason Reason, int? WinnerSeat, int[] WinLine, bool YouWon) : BotServerMessage;
public sealed record BotAck(string Id, Guid? MatchId, long? Seq) : BotServerMessage;
public sealed record BotError(string? Id, Guid? MatchId, string Code, string Message) : BotServerMessage;
public sealed record BotPing(long T) : BotServerMessage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BotSeek), "seek")] [JsonDerivedType(typeof(BotUnseek), "unseek")]
[JsonDerivedType(typeof(BotAcceptChallenge), "acceptChallenge")] [JsonDerivedType(typeof(BotAction), "action")]
[JsonDerivedType(typeof(BotResign), "resign")] [JsonDerivedType(typeof(BotGetState), "getState")] [JsonDerivedType(typeof(BotPong), "pong")]
[JsonDerivedType(typeof(BotEmote), "emote")]
public abstract record BotClientMessage;
/// <summary>Bot emote (EmoteCatalog id); rate-limited like a player's.</summary>
public sealed record BotEmote(string? Id, Guid MatchId, string EmoteId) : BotClientMessage;
public sealed record BotSeek(string? Id, TimeControl[] TimeControls, int MaxConcurrentGames) : BotClientMessage;
public sealed record BotUnseek(string? Id) : BotClientMessage;
public sealed record BotAcceptChallenge(string Id, string Code) : BotClientMessage;
public sealed record BotAction(string Id, Guid MatchId, int Ply, ActionKind Kind, int? Cell) : BotClientMessage;
public sealed record BotResign(string? Id, Guid MatchId) : BotClientMessage;
public sealed record BotGetState(string? Id, Guid MatchId) : BotClientMessage;
public sealed record BotPong(long T) : BotClientMessage;
