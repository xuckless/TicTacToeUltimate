using System.Text.Json;
using System.Text.Json.Nodes;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.State;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>User settings persisted as JSON under <c>ttt.settings</c>; theme mirrored to <c>ttt.theme</c> for the pre-paint bootstrap.</summary>
public sealed class SettingsStore(IKeyValueStore kv, ISoundService sound) : StoreBase<SettingsState>(SettingsState.Default), ISettingsStore
{
    /// <summary>Storage key.</summary>
    public const string Key = "ttt.settings";
    /// <summary>Theme key read by the inline bootstrap script.</summary>
    public const string ThemeKey = "ttt.theme";

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        string? json = null;
        try { json = await kv.GetAsync(Key); } catch (Exception) { /* storage unavailable */ }
        var loaded = Parse(json);
        Update(_ => loaded);
        Apply(loaded);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Func<SettingsState, SettingsState> change)
    {
        var next = Update(change);
        Apply(next);
        try
        {
            await kv.SetAsync(Key, Serialize(next));
            if (next.Theme == ThemePreference.System) await kv.RemoveAsync(ThemeKey);
            else await kv.SetAsync(ThemeKey, next.Theme == ThemePreference.Light ? "light" : "dark");
        }
        catch (Exception) { /* keep in memory */ }
    }

    private void Apply(SettingsState s)
    {
        try
        {
            sound.SetVolumes(s.Sfx ? s.SfxVolume : 0, s.Music ? s.MusicVolume : 0);
            sound.SetMusic(s.Music);
        }
        catch (Exception) { /* audio optional */ }
    }

    /// <summary>Serializes settings (manual, trim-safe).</summary>
    public static string Serialize(SettingsState s) => new JsonObject
    {
        ["theme"] = s.Theme.ToString(), ["sfx"] = s.Sfx, ["music"] = s.Music, ["haptics"] = s.Haptics,
        ["impactFrames"] = s.ImpactFrames, ["motion"] = s.Motion.ToString(), ["guidedMode"] = s.ThreatHints,
        ["muteOpponentEmotes"] = s.MuteOpponentEmotes, ["sfxVolume"] = s.SfxVolume, ["musicVolume"] = s.MusicVolume,
        ["language"] = s.Language,
    }.ToJsonString();

    /// <summary>Parses settings; missing or invalid fields fall back to defaults.</summary>
    public static SettingsState Parse(string? json)
    {
        var d = SettingsState.Default;
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return d;
            return new SettingsState(
                Enum.TryParse<ThemePreference>(Str(o, "theme"), out var theme) ? theme : d.Theme,
                Bool(o, "sfx", d.Sfx), Bool(o, "music", d.Music), Bool(o, "haptics", d.Haptics), Bool(o, "impactFrames", d.ImpactFrames),
                Enum.TryParse<MotionPreference>(Str(o, "motion"), out var motion) ? motion : d.Motion,
                Bool(o, "guidedMode", d.ThreatHints), Bool(o, "muteOpponentEmotes", d.MuteOpponentEmotes),
                Volume(o, "sfxVolume", d.SfxVolume), Volume(o, "musicVolume", d.MusicVolume)) { Language = Str(o, "language") ?? d.Language };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return d;
        }
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;

    private static bool Bool(JsonObject o, string key, bool fallback)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static double Volume(JsonObject o, string key, double fallback)
        => o[key] is JsonValue v && v.TryGetValue<double>(out var x) ? Math.Clamp(x, 0, 1) : fallback;
}
