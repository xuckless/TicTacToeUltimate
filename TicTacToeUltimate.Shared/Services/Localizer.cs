using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TicTacToeUltimate.Shared.Services;

/// <summary>
/// UI text in 26 languages (owner decision 2026-10-16: the Supercell set, system-detected, override in Settings).
/// Catalogs are flat JSON files in wwwroot/i18n/{code}.json: "key": "text", plurals as "key|one", "key|few", "key|other" (CLDR
/// categories), placeholders as {0}, {1}. English is embedded (always available); other languages are fetched when chosen.
/// Missing keys fall back to English, then to the key itself. Use the static <see cref="L"/> shortcut in markup and helpers.
/// </summary>
public sealed class Localizer
{
    /// <summary>Supported languages: code, native name, right-to-left.</summary>
    public static readonly IReadOnlyList<(string Code, string Native, bool Rtl)> Languages =
    [
        ("en", "English", false), ("fr", "Français", false), ("de", "Deutsch", false), ("es", "Español", false), ("it", "Italiano", false),
        ("nl", "Nederlands", false), ("pt-BR", "Português (Brasil)", false), ("ru", "Русский", false), ("tr", "Türkçe", false), ("pl", "Polski", false),
        ("ja", "日本語", false), ("ko", "한국어", false), ("zh-Hans", "简体中文", false), ("zh-Hant", "繁體中文", false), ("ar", "العربية", true),
        ("he", "עברית", true), ("fa", "فارسی", true), ("th", "ไทย", false), ("vi", "Tiếng Việt", false), ("id", "Bahasa Indonesia", false),
        ("ms", "Bahasa Melayu", false), ("sv", "Svenska", false), ("no", "Norsk", false), ("da", "Dansk", false), ("fi", "Suomi", false),
    ];

    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEmbeddedEnglish);
    private Dictionary<string, string> _current = new(StringComparer.Ordinal);

    /// <summary>Creates the localizer (English) and makes it the one <see cref="L"/> uses.</summary>
    public Localizer() => L.Current = this;

    /// <summary>Active language code (one of <see cref="Languages"/>).</summary>
    public string Language { get; private set; } = "en";
    /// <summary>True for Arabic, Hebrew, Persian.</summary>
    public bool Rtl => Languages.FirstOrDefault(l => l.Code == Language).Rtl;
    /// <summary>Raised after the language changed (the app re-renders).</summary>
    public event Action? Changed;

    /// <summary>Text for a key.</summary>
    public string this[string key] => _current.TryGetValue(key, out var v) ? v : English.Value.TryGetValue(key, out var e) ? e : key;

    /// <summary>Text with {0}… placeholders filled (culture-aware number formatting).</summary>
    public string Format(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, this[key], args); }
        catch (FormatException) { return this[key]; }
    }

    /// <summary>Plural-aware text: "key|one" / "key|few" / … / "key|other", with {0} = <paramref name="n"/>.</summary>
    public string Plural(string key, long n, params object?[] args)
    {
        var cat = PluralRules.Category(Language, n);
        var k = Has($"{key}|{cat}") ? $"{key}|{cat}" : $"{key}|other";
        object?[] all = [n, .. args];
        try { return string.Format(CultureInfo.CurrentCulture, this[k], all); }
        catch (FormatException) { return this[k]; }
    }

    private bool Has(string key) => _current.ContainsKey(key) || English.Value.ContainsKey(key);

    /// <summary>Best supported language for a list of BCP 47 tags (browser / device order).</summary>
    public static string Match(IEnumerable<string?> tags)
    {
        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Trim();
            var lower = t.ToLowerInvariant();
            if (lower.StartsWith("zh", StringComparison.Ordinal))
                return lower.Contains("hant") || lower.EndsWith("-tw", StringComparison.Ordinal) || lower.EndsWith("-hk", StringComparison.Ordinal) || lower.EndsWith("-mo", StringComparison.Ordinal) ? "zh-Hant" : "zh-Hans";
            if (lower.StartsWith("pt", StringComparison.Ordinal)) return "pt-BR";
            if (lower is "nb" or "nn" || lower.StartsWith("nb-", StringComparison.Ordinal) || lower.StartsWith("nn-", StringComparison.Ordinal)) return "no";
            if (lower.StartsWith("iw", StringComparison.Ordinal)) return "he";
            if (lower.StartsWith("in", StringComparison.Ordinal) && (lower.Length == 2 || lower[2] == '-')) return "id";
            var exact = Languages.FirstOrDefault(l => string.Equals(l.Code, t, StringComparison.OrdinalIgnoreCase)).Code;
            if (exact is not null) return exact;
            var basePart = lower.Split('-')[0];
            var byBase = Languages.FirstOrDefault(l => l.Code.Split('-')[0].Equals(basePart, StringComparison.OrdinalIgnoreCase)).Code;
            if (byBase is not null) return byBase;
        }
        return "en";
    }

    /// <summary>Switches language. <paramref name="loadCatalog"/> returns the JSON text of a catalog (null = unavailable).</summary>
    public async Task SetLanguageAsync(string code, Func<string, Task<string?>> loadCatalog)
    {
        if (!Languages.Any(l => l.Code == code)) code = "en";
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code != "en" && await loadCatalog(code) is { Length: > 0 } json)
        {
            try { catalog = Parse(json); }
            catch (JsonException) { code = "en"; }
        }
        _current = code == "en" ? new(StringComparer.Ordinal) : catalog;
        Language = code;
        var culture = CultureFor(code);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Changed?.Invoke();
    }

    private static CultureInfo CultureFor(string code)
    {
        try { return CultureInfo.GetCultureInfo(code switch { "no" => "nb-NO", "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", _ => code }); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>Flat catalog; keys starting with "_" are metadata (e.g. "_machine": true).</summary>
    public static Dictionary<string, string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (!p.Name.StartsWith('_') && p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString()!;
        return d;
    }

    private static Dictionary<string, string> LoadEmbeddedEnglish()
    {
        using var s = typeof(Localizer).Assembly.GetManifestResourceStream("i18n.en.json");
        if (s is null) return new(StringComparer.Ordinal);
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    /// <summary>All English keys (tests check every catalog against them).</summary>
    public static IReadOnlyDictionary<string, string> EnglishCatalog => English.Value;
}

/// <summary>Static shortcut: <c>L.T("home.title")</c>, <c>L.F("queue.wait", secs)</c>, <c>L.P("games.count", n)</c>.</summary>
public static class L
{
    private static Localizer? _current;

    /// <summary>The app's localizer (an English one until the app creates its own).</summary>
    public static Localizer Current { get => _current ??= new Localizer(); set => _current = value; }

    public static string T(string key) => Current[key];
    public static string F(string key, params object?[] args) => Current.Format(key, args);
    public static string P(string key, long n, params object?[] args) => Current.Plural(key, n, args);
}

/// <summary>CLDR cardinal plural categories for the supported languages (integers).</summary>
public static class PluralRules
{
    public static string Category(string lang, long n)
    {
        var a = Math.Abs(n);
        var m10 = a % 10; var m100 = a % 100;
        return lang switch
        {
            "ja" or "ko" or "zh-Hans" or "zh-Hant" or "th" or "vi" or "id" or "ms" or "tr" => "other",
            "fr" or "pt-BR" or "fa" => a is 0 or 1 ? "one" : "other",
            "ru" => m10 == 1 && m100 != 11 ? "one" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "few" : "many",
            "pl" => a == 1 ? "one" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "few" : "many",
            "ar" => a switch { 0 => "zero", 1 => "one", 2 => "two", _ when m100 is >= 3 and <= 10 => "few", _ when m100 is >= 11 and <= 99 => "many", _ => "other" },
            "he" => a switch { 1 => "one", 2 => "two", _ => "other" },
            _ => a == 1 ? "one" : "other",
        };
    }
}
