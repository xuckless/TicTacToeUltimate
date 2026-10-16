using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Matchmaking;
using TicTacToeUltimate.Server.Persistence;

namespace TicTacToeUltimate.Server.Moderation;

/// <summary>
/// Penalties and their enforcement. Auto-moderation records strikes; the ladder turns strikes within
/// <see cref="ModerationOptions.StrikeWindowDays"/> into a warning, then 1 h / 24 h / 7 d chat mutes. Bans come only from an
/// admin and cover the account plus every device it used. Active penalties are cached briefly (one server instance).
/// </summary>
public sealed class ModerationService(IModerationRepository repo, IUserRepository users, IUserNotifier notifier, TimeProvider time,
    IOptions<TttOptions> options, IServiceProvider services, ILogger<ModerationService> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<PenaltyRecord> Active)> _active = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PenaltyRecord? Ban)> _deviceBans = new();
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _deviceTouched = new();
    private volatile HashSet<Guid>? _shadow;

    private ModerationOptions Opts => options.Value.Moderation;

    // ---------- reads ----------

    /// <summary>Penalties in force (ban, chat mute, shadow).</summary>
    public async Task<IReadOnlyList<PenaltyRecord>> ActiveAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_active.TryGetValue(userId, out var c) && now - c.At < CacheFor) return c.Active.Where(p => p.ActiveAt(now)).ToList();
        var list = (await repo.PenaltiesForAsync(userId, 50, ct)).Where(p => p.ActiveAt(now)).ToList();
        _active[userId] = (now, list);
        return list;
    }

    /// <summary>The ban on this account or device, if any.</summary>
    public async Task<PenaltyRecord?> BanAsync(Guid? userId, string? deviceHash, CancellationToken ct)
    {
        if (userId is { } u && (await ActiveAsync(u, ct)).FirstOrDefault(p => p.Kind == PenaltyKind.Ban) is { } ban) return ban;
        return deviceHash is null ? null : await DeviceBanAsync(deviceHash, ct);
    }

    private async Task<PenaltyRecord?> DeviceBanAsync(string hash, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_deviceBans.TryGetValue(hash, out var c) && now - c.At < CacheFor) return c.Ban is { } b && b.ActiveAt(now) ? b : null;
        PenaltyRecord? found = null;
        foreach (var id in await repo.DeviceBanPenaltiesAsync(hash, ct))
            if (await repo.GetPenaltyAsync(id, ct) is { } p && p.ActiveAt(now)) { found = p; break; }
        _deviceBans[hash] = (now, found);
        return found;
    }

    /// <summary>The chat mute in force, if any.</summary>
    public async Task<PenaltyRecord?> ChatMuteAsync(Guid userId, CancellationToken ct) =>
        (await ActiveAsync(userId, ct)).Where(p => p.Kind == PenaltyKind.ChatMute).OrderByDescending(p => p.EndsAt ?? DateTimeOffset.MaxValue).FirstOrDefault();

    /// <summary>Shadow-pool membership (anti-cheat): flagged players are matched only with bots or each other.</summary>
    public async Task<bool> IsShadowAsync(Guid userId, CancellationToken ct)
    {
        var set = _shadow;
        if (set is null) { set = [.. await repo.ShadowUsersAsync(ct)]; _shadow = set; }
        return set.Contains(userId);
    }

    /// <summary>Synchronous shadow check for the matchmaker loop (false until the set has loaded once).</summary>
    public bool IsShadowCached(Guid userId) => _shadow?.Contains(userId) == true;

    /// <summary>GET /api/me/standing.</summary>
    public async Task<StandingDto> StandingAsync(Guid userId, CancellationToken ct)
    {
        var all = await repo.PenaltiesForAsync(userId, 50, ct);
        var now = time.GetUtcNow();
        var active = new List<PenaltyDto>();
        foreach (var p in all.Where(p => p.ActiveAt(now) && p.Kind != PenaltyKind.Shadow)) active.Add(await ToDtoAsync(p, ct));
        var warn = all.FirstOrDefault(p => p.Kind == PenaltyKind.Warn && p.RevokedAt is null && now - p.StartsAt < TimeSpan.FromDays(Opts.StrikeWindowDays));
        return new StandingDto(active.ToArray(), warn is null ? null : await ToDtoAsync(warn, ct));
    }

    /// <summary>Player-facing view of a penalty (never for shadow).</summary>
    public async Task<PenaltyDto> ToDtoAsync(PenaltyRecord p, CancellationToken ct)
    {
        var appeal = await repo.AppealForPenaltyAsync(p.Id, ct);
        var appealable = p.Kind is PenaltyKind.Ban or PenaltyKind.ChatMute && p.RevokedAt is null && appeal is null;
        return new PenaltyDto(p.Id, PenaltyKinds.ToDb(p.Kind), p.Reason, p.StartsAt.ToUnixTimeMilliseconds(), p.EndsAt?.ToUnixTimeMilliseconds(),
            appealable, appeal?.Status);
    }

    // ---------- devices ----------

    /// <summary>Salted hash of a client device id (raw ids are never stored for accounts).</summary>
    public string DeviceHash(string deviceId)
    {
        var salt = Opts.DeviceSalt is { Length: > 0 } s ? s : options.Value.Auth.Dev.SigningKey is { Length: > 0 } k ? k : "ttt-device";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(deviceId.Trim()));
        return Convert.ToHexStringLower(mac.AsSpan(0, 16));
    }

    /// <summary>Remembers that a user used a device (at most once an hour per pair).</summary>
    public async Task TouchDeviceAsync(Guid userId, string deviceHash, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_deviceTouched.TryGetValue((userId, deviceHash), out var at) && now - at < TimeSpan.FromHours(1)) return;
        _deviceTouched[(userId, deviceHash)] = now;
        await repo.TouchDeviceAsync(userId, deviceHash, now, ct);
    }

    // ---------- writes ----------

    /// <summary>
    /// Records an auto-moderation strike and applies the ladder step it reaches: 1 → warn, 2 → 1 h mute, 3 → 24 h, 4+ → 7 d.
    /// Returns the penalty applied.
    /// </summary>
    public async Task<PenaltyRecord> StrikeAsync(Guid userId, string kind, string? text, object? detail, CancellationToken ct, long? reportId = null)
    {
        var now = time.GetUtcNow();
        await repo.AddEventAsync(new ModerationEventRecord(0, userId, kind, text, JsonSerializer.Serialize(detail ?? new { }), now), ct);
        var strikes = await repo.CountStrikesSinceAsync(userId, now.AddDays(-Opts.StrikeWindowDays), ct);
        var reason = kind switch
        {
            "username_block" => "Your username broke the naming rules.",
            "report_confirmed" => "A message you sent was reported and confirmed as abusive.",
            _ => "A message you sent broke the chat rules.",
        };
        if (strikes <= 1) return await ApplyAsync(userId, PenaltyKind.Warn, reason, null, "auto", null, reportId, ct);
        var minutes = Opts.MuteMinutes[Math.Min(strikes - 2, Opts.MuteMinutes.Length - 1)];
        return await ApplyAsync(userId, PenaltyKind.ChatMute, reason, TimeSpan.FromMinutes(minutes), "auto", null, reportId, ct);
    }

    /// <summary>Applies a penalty and enforces it now (ban: devices, live connections, queue, matches; reset: username).</summary>
    public async Task<PenaltyRecord> ApplyAsync(Guid userId, PenaltyKind kind, string reason, TimeSpan? duration, string source, string? by,
        long? reportId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var p = await repo.AddPenaltyAsync(new PenaltyRecord(0, userId, kind, reason, now, duration is { } d ? now + d : null, source, reportId,
            by ?? source, null), ct);
        Invalidate(userId);
        switch (kind)
        {
            case PenaltyKind.Ban:
                foreach (var h in await repo.DevicesForAsync(userId, ct)) { await repo.AddDeviceBanAsync(h, p.Id, ct); _deviceBans.TryRemove(h, out _); }
                await KickAsync(userId, ct);
                break;
            case PenaltyKind.UsernameReset:
                await users.ResetUsernameAsync(userId, ct);
                break;
            case PenaltyKind.Shadow:
                await repo.SetShadowAsync(userId, true, ct);
                _shadow = null;
                break;
        }
        if (kind != PenaltyKind.Shadow)
        {
            var dto = await ToDtoAsync(p, ct);
            try { await notifier.ToUserAsync(userId, c => c.PenaltyApplied(dto)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "PenaltyApplied push failed"); }
        }
        log.LogInformation("Penalty {Kind} on {User} ({Source}): {Reason}", kind, userId, source, reason);
        return p;
    }

    /// <summary>Revokes a penalty (admin, or an approved appeal).</summary>
    public async Task<bool> RevokeAsync(long penaltyId, string by, CancellationToken ct)
    {
        var p = await repo.GetPenaltyAsync(penaltyId, ct);
        if (p is null || !await repo.RevokePenaltyAsync(penaltyId, by, time.GetUtcNow(), ct)) return false;
        Invalidate(p.UserId);
        _deviceBans.Clear();
        if (p.Kind == PenaltyKind.Shadow)
        {
            var stillShadow = (await repo.PenaltiesForAsync(p.UserId, 50, ct)).Any(x => x.Kind == PenaltyKind.Shadow && x.Id != p.Id && x.ActiveAt(time.GetUtcNow()));
            if (!stillShadow) { await repo.SetShadowAsync(p.UserId, false, ct); _shadow = null; }
        }
        return true;
    }

    /// <summary>POST /api/appeals: one appeal per ban or mute.</summary>
    public async Task<string?> AppealAsync(Guid userId, AppealRequestDto req, CancellationToken ct)
    {
        var text = req.Text?.Trim() ?? "";
        if (text.Length is 0 or > 1000) return ErrorCodes.Validation;
        var p = await repo.GetPenaltyAsync(req.PenaltyId, ct);
        if (p is null || p.UserId != userId || p.Kind is not (PenaltyKind.Ban or PenaltyKind.ChatMute)) return ErrorCodes.Validation;
        var a = await repo.AddAppealAsync(new AppealRecord(0, p.Id, userId, text, "open", time.GetUtcNow(), null, null), ct);
        return a is null ? ErrorCodes.AlreadyAppealed : null;
    }

    private void Invalidate(Guid userId) => _active.TryRemove(userId, out _);

    /// <summary>A new ban takes effect immediately: drop live sockets, leave queues, resign live (non-daily) matches.</summary>
    private async Task KickAsync(Guid userId, CancellationToken ct)
    {
        if (services.GetService(typeof(Matchmaker)) is Matchmaker mm) mm.Post(new LeaveQueueCommand(userId));
        if (services.GetService(typeof(IMatchGateway)) is IMatchGateway gateway)
            foreach (var matchId in gateway.ActiveMatchesFor(userId))
            {
                try { await gateway.ResignAsync(matchId, userId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Resign on ban failed for {Match}", matchId); }
            }
        (services.GetService(typeof(ServerDrain)) as ServerDrain)?.CloseUser(userId);
    }
}
