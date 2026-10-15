using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Engine.Analysis;

namespace TicTacToeUltimate.Contracts;

public sealed record DevUserDto(Guid UserId, string DisplayName, AccountKind Kind, TierDto? Tier, int? Rating);
public sealed record DevTokenRequestDto(Guid? UserId, string? NewUsername);     // exactly one set
public sealed record GuestTokenRequestDto(string DeviceId);                       // client-generated stable Guid string
public sealed record TokenResponseDto(string AccessToken, long ExpiresAtMs, MeDto Me);
public sealed record SetUsernameDto(string Username, bool AcceptTos);
public sealed record UpdateProfileDto(string? AvatarId, string? Country, string? TimeZone);
