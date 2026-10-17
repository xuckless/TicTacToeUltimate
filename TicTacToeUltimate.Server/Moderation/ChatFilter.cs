using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TicTacToeUltimate.Server.Moderation;

/// <summary>What the filter decided about a message.</summary>
public enum FilterVerdict
{
    /// <summary>Deliver.</summary>
    Allow,
    /// <summary>Deliver, then let the AI check it (mild word, or a severe word hidden inside another word).</summary>
    Uncertain,
    /// <summary>Don't deliver, no strike (links, emails, phone numbers: spam and personal info).</summary>
    Reject,
    /// <summary>Don't deliver and record a strike (abusive word).</summary>
    Block,
}

public sealed record FilterResult(FilterVerdict Verdict, string? Term = null, string? Reason = null);

/// <summary>
/// Multilingual word filter for chat and usernames (owner decision 2026-10-16: blocked messages are not delivered and the
/// sender gets a strike). Lists live in Social/Data/words/{lang}.txt; every list is checked whatever the player's language.
/// <list type="bullet">
/// <item><c>!term</c> severe: a whole word blocks; hidden inside a longer word it is only Uncertain (avoids "Scunthorpe").</item>
/// <item><c>term</c>: a whole word blocks.</item>
/// <item><c>?term</c> mild: Uncertain (the AI decides from context).</item>
/// </list>
/// Text is normalised first: case, zero-width characters, leetspeak (s4 → sa), punctuation inside words (f.u.c.k),
/// spaced-out letters (f u c k), repeated letters (fuuuck). Diacritics are dropped only for terms written without them, so
/// Vietnamese "các" never matches "cặc". Scripts without spaces (CJK, Thai) match terms anywhere in the text.
/// </summary>
public sealed partial class ChatFilter
{
    private sealed record Term(string Text, bool Severe, bool Mild, bool KeepMarks, bool Unspaced);

    private readonly Term[] _terms;

    public ChatFilter() : this(Load()) { }

    public ChatFilter(IEnumerable<string> lines)
    {
        _terms = lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => (Severe: l.StartsWith('!'), Mild: l.StartsWith('?'), Raw: l.TrimStart('!', '?')))
            .Where(x => x.Raw.Length > 0)
            .Select(x =>
            {
                var keepMarks = x.Raw.Any(c => c > 0x7F && char.IsLetter(c) && Fold(c.ToString()) != c.ToString());
                var unspaced = x.Raw.Any(IsUnspacedScript);
                return new Term(Collapse(Normalize(x.Raw, keepMarks)), x.Severe, x.Mild, keepMarks, unspaced);
            })
            .Where(t => t.Text.Length > 0)
            .DistinctBy(t => (t.Text, t.KeepMarks))
            .ToArray();
    }

    private static IEnumerable<string> Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Social", "Data", "words");
        try
        {
            if (Directory.Exists(dir)) return Directory.EnumerateFiles(dir, "*.txt").SelectMany(File.ReadAllLines).ToArray();
        }
        catch (IOException) { }
        return ["!nigger", "!faggot", "!cunt", "fuck", "shit", "bitch", "whore", "slut", "asshole", "?idiot", "?stupid"];
    }

    /// <summary>Checks a chat message.</summary>
    public FilterResult Check(string text)
    {
        if (LinkRegex().IsMatch(text) && !OwnLinkRegex().IsMatch(text)) return new(FilterVerdict.Reject, null, "link");
        if (EmailRegex().IsMatch(text)) return new(FilterVerdict.Reject, null, "email");
        if (PhoneRegex().IsMatch(text)) return new(FilterVerdict.Reject, null, "phone");

        var plain = Words(text, keepMarks: false);
        var marked = Words(text, keepMarks: true);
        var raw = Collapse(Normalize(text, keepMarks: true));
        FilterResult result = new(FilterVerdict.Allow);
        foreach (var t in _terms)
        {
            var words = t.KeepMarks ? marked : plain;
            var whole = t.Unspaced ? raw.Contains(t.Text, StringComparison.Ordinal) : words.Contains(t.Text);
            if (whole)
            {
                if (t.Mild) result = Max(result, new(FilterVerdict.Uncertain, t.Text, "mild"));
                else return new(FilterVerdict.Block, t.Text, t.Severe ? "severe" : "word");
            }
            else if (t.Severe && t.Text.Length >= 4 && words.Any(w => w.Contains(t.Text, StringComparison.Ordinal)))
                result = Max(result, new(FilterVerdict.Uncertain, t.Text, "inside a word"));
        }
        return result;
    }

    /// <summary>
    /// Usernames: terms of 5+ letters are refused anywhere in the name (minus digits and _); shorter ones only as a whole part
    /// of it ("grape_master" is fine, "rape_x" is not). The AI check catches the rest.
    /// </summary>
    public bool UsernameBlocked(string username)
    {
        var norm = Normalize(username.Replace('_', ' '), keepMarks: false);
        var squashed = Collapse(new string(norm.Where(char.IsLetter).ToArray()));
        var parts = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => Collapse(new string(p.Where(char.IsLetter).ToArray()))).ToHashSet();
        return _terms.Where(t => !t.Mild && !t.KeepMarks).Any(t => t.Text.Length >= 5 ? squashed.Contains(t.Text, StringComparison.Ordinal) : parts.Contains(t.Text));
    }

    private static FilterResult Max(FilterResult a, FilterResult b) => b.Verdict > a.Verdict ? b : a;

    /// <summary>Words of the text after normalisation; runs of single letters ("f u c k") are joined into one word.</summary>
    private static HashSet<string> Words(string text, bool keepMarks)
    {
        var tokens = Normalize(text, keepMarks).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetter).ToArray())).Where(t => t.Length > 0).ToList();
        var words = new HashSet<string>(StringComparer.Ordinal);
        var run = new StringBuilder();
        foreach (var t in tokens)
        {
            if (t.Length == 1) { run.Append(t); continue; }
            if (run.Length > 1) words.Add(Collapse(run.ToString()));
            run.Clear();
            words.Add(Collapse(t));
        }
        if (run.Length > 1) words.Add(Collapse(run.ToString()));
        return words;
    }

    /// <summary>Lowercase, no zero-width characters, leetspeak mapped, separators as spaces, diacritics dropped unless kept.</summary>
    private static string Normalize(string s, bool keepMarks)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            if (ch is '​' or '‌' or '‍' or '﻿' or '­') continue;
            sb.Append(ch switch
            {
                '0' => 'o', '1' or '!' or '|' => 'i', '3' => 'e', '4' or '@' => 'a', '5' or '$' => 's', '7' => 't', '8' => 'b',
                _ when char.IsWhiteSpace(ch) => ' ',
                _ => ch,
            });
        }
        var r = sb.ToString();
        return keepMarks ? r.Normalize(NormalizationForm.FormC) : Fold(r);
    }

    private static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch is 'đ' ? 'd' : ch is 'ł' ? 'l' : ch is 'ø' ? 'o' : ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>"fuuuuck" → "fuck", "asshole" → "ashole" (applied to terms and text alike).</summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (sb.Length == 0 || sb[^1] != ch) sb.Append(ch);
        return sb.ToString();
    }

    private static bool IsUnspacedScript(char c) =>
        c is >= '぀' and <= 'ヿ'    // Hiragana, Katakana
        or >= '一' and <= '鿿'      // CJK ideographs
        or >= '가' and <= '힯'      // Hangul syllables
        or >= '฀' and <= '๿';     // Thai

    [GeneratedRegex(@"(https?://|www\.)\S+|\b[\w-]+\.(com|net|org|io|gg|ru|xyz|ly|me|co|app|link|site|online|top|tk|cn)\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"alidirect\.dev", RegexOptions.IgnoreCase, 200)]
    private static partial Regex OwnLinkRegex();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.None, 200)]
    private static partial Regex EmailRegex();
    [GeneratedRegex(@"(\+?\d[\d\s().-]{7,}\d)", RegexOptions.None, 200)]
    private static partial Regex PhoneRegex();
}
