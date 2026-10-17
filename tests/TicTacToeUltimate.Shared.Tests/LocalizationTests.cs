using System.Text.RegularExpressions;
using TicTacToeUltimate.Shared.Services;

namespace TicTacToeUltimate.Shared.Tests;

/// <summary>Every translation must carry every English key, the same {n} placeholders, and the plural forms its language needs.</summary>
public sealed partial class LocalizationTests
{
    private static readonly string I18n = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TicTacToeUltimate.Shared", "wwwroot", "i18n");

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var (code, _, _) in Localizer.Languages) if (code != "en") data.Add(code);
        return data;
    }

    private static Dictionary<string, string> Load(string code) => Localizer.Parse(File.ReadAllText(Path.Combine(I18n, $"{code}.json")));

    [Theory]
    [MemberData(nameof(Languages))]
    public void Catalog_is_complete_and_placeholders_match(string code)
    {
        var en = Load("en");
        var t = Load(code);
        var missing = new List<string>();
        var bad = new List<string>();
        foreach (var (key, english) in en)
        {
            var baseKey = key.Split('|')[0];
            var isPlural = key.Contains('|');
            if (isPlural)
            {
                // English has one/other; the translation needs "other" plus whatever categories its language uses.
                if (!t.ContainsKey($"{baseKey}|other")) missing.Add($"{baseKey}|other");
                continue;
            }
            if (!t.TryGetValue(key, out var text)) { missing.Add(key); continue; }
            if (!Placeholders(english).SetEquals(Placeholders(text))) bad.Add($"{key}: '{english}' vs '{text}'");
        }
        foreach (var key in t.Keys.Where(k => k.Contains('|')))
        {
            var english = en.FirstOrDefault(kv => kv.Key.StartsWith(key.Split('|')[0] + "|", StringComparison.Ordinal)).Value;
            if (english is not null && !Placeholders(t[key]).IsSubsetOf(Placeholders(english).Append("{0}").ToHashSet())) bad.Add($"{key}: placeholders");
        }
        Assert.True(missing.Count == 0, $"{code} misses {missing.Count} keys: {string.Join(", ", missing.Take(15))}");
        Assert.True(bad.Count == 0, $"{code} placeholder mismatches: {string.Join(" | ", bad.Take(10))}");
        Assert.Empty(t.Keys.Where(k => !en.ContainsKey(k) && !en.Keys.Any(e => e.Split('|')[0] == k.Split('|')[0])));
    }

    [Theory]
    [InlineData("ru", 1, "one")] [InlineData("ru", 3, "few")] [InlineData("ru", 11, "many")] [InlineData("ru", 21, "one")]
    [InlineData("pl", 22, "few")] [InlineData("pl", 25, "many")]
    [InlineData("ar", 0, "zero")] [InlineData("ar", 2, "two")] [InlineData("ar", 7, "few")] [InlineData("ar", 15, "many")] [InlineData("ar", 100, "other")]
    [InlineData("fr", 0, "one")] [InlineData("en", 0, "other")] [InlineData("ja", 1, "other")]
    public void Plural_categories(string lang, long n, string expected) => Assert.Equal(expected, PluralRules.Category(lang, n));

    [Theory]
    [InlineData("pt-PT", "pt-BR")] [InlineData("zh-TW", "zh-Hant")] [InlineData("zh-CN", "zh-Hans")] [InlineData("nb-NO", "no")]
    [InlineData("de-AT", "de")] [InlineData("xx", "en")] [InlineData("iw", "he")]
    public void Device_languages_map_to_catalogs(string tag, string code) => Assert.Equal(code, Localizer.Match([tag]));

    [Fact]
    public async Task Switching_to_arabic_is_right_to_left_and_falls_back_to_english_per_key()
    {
        var loc = new Localizer();
        try
        {
            await loc.SetLanguageAsync("ar", c => Task.FromResult<string?>("""{ "settings.theme": "السمة" }"""));
            Assert.True(loc.Rtl);
            Assert.Equal("السمة", L.T("settings.theme"));
            Assert.Equal(Localizer.EnglishCatalog["settings.sign_out"], L.T("settings.sign_out"));
        }
        finally { await loc.SetLanguageAsync("en", _ => Task.FromResult<string?>(null)); }
    }

    private static HashSet<string> Placeholders(string s) => [.. PlaceholderRegex().Matches(s).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();
}
