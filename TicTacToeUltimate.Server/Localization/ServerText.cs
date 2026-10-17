using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TicTacToeUltimate.Server.Abstractions.Persistence;

namespace TicTacToeUltimate.Server.Localization;

/// <summary>
/// Text the server writes for players (push notifications, in-app notifications) in each player's language
/// (players.locale, sent by the app). Catalogs: Localization/{code}.json next to the binary; English is the fallback.
/// </summary>
public sealed class ServerText(IUserRepository users)
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Catalogs = new();

    /// <summary>Text for a player (their saved language, else English).</summary>
    public async Task<string> ForAsync(Guid userId, string key, params object?[] args)
    {
        var locale = await users.GetLocaleAsync(userId, CancellationToken.None).ConfigureAwait(false);
        return Get(locale, key, args);
    }

    /// <summary>The player's language (null = English).</summary>
    public Task<string?> LocaleAsync(Guid userId) => users.GetLocaleAsync(userId, CancellationToken.None);

    public static string Get(string? locale, string key, params object?[] args)
    {
        var text = Catalog(locale ?? "en").TryGetValue(key, out var t) ? t : Catalog("en").TryGetValue(key, out var e) ? e : key;
        if (args.Length == 0) return text;
        try { return string.Format(CultureFor(locale), text, args); }
        catch (FormatException) { return text; }
    }

    private static CultureInfo CultureFor(string? code)
    {
        try { return code is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(code switch { "no" => "nb-NO", "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", _ => code }); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private static IReadOnlyDictionary<string, string> Catalog(string code) => Catalogs.GetOrAdd(code, c =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", $"{c}.json");
        if (c.Contains('/') || c.Contains('\\') || c.Contains("..", StringComparison.Ordinal) || !File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && !p.Name.StartsWith('_'))
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new Dictionary<string, string>(); }
    });
}
