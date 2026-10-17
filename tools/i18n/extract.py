#!/usr/bin/env python3
"""Moves literal UI text out of Razor markup into the English catalog (TicTacToeUltimate.Shared/wwwroot/i18n/en.json).

Text nodes between tags and user-facing attributes (Title, Label, Placeholder, aria-label, title, Hint, alt, Text, ...)
become @L.T("key"). Identical English text shares one key. Text mixed with @expressions is reported, not changed: those
need L.F("key", args) written by hand so translators see the whole sentence.

  python3 tools/i18n/extract.py            # report only
  python3 tools/i18n/extract.py --write    # rewrite files + update en.json
"""
import html, json, os, re, sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "..", "TicTacToeUltimate.Shared")
CATALOG = os.path.join(ROOT, "wwwroot", "i18n", "en.json")
SKIP_FILES = {"Icon.razor", "Bert.razor", "PieceBuddy.razor", "Wallpaper.razor", "BertFlyby.razor", "PieceGlyph.razor", "_Imports.razor"}
SKIP_DIRS = {"Fakes"}
BRAND = {"Tic Tac Toe", "Ultimate", "Tic Tac Toe Ultimate", "TTT", "Bert"}
ATTRS = ["Title", "Label", "Placeholder", "placeholder", "aria-label", "title", "Hint", "alt", "Text", "Sub", "Intro", "Caption",
         "Kicker", "Subtitle", "Description", "Heading", "Message", "Prompt", "EmptyText", "Cta", "ButtonText", "Tooltip"]
ATTR_RE = re.compile(r'(?<![\w-])(' + "|".join(re.escape(a) for a in ATTRS) + r')="([^"@]*?[A-Za-z][^"@]*?)"')
TEXT_RE = re.compile(r'>([^<>]*?)<', re.S)
WORD = re.compile(r"[A-Za-z]{2,}")

def slug(text):
    words = re.findall(r"[a-z0-9]+", text.lower())[:5]
    return "_".join(words)[:40] or "text"

def prefix(path):
    stem = os.path.splitext(os.path.basename(path))[0]
    stem = re.sub(r"Page$", "", stem) or stem
    return stem[0].lower() + stem[1:]

def markup_end(src):
    """Index where the @code block starts (markup before it)."""
    m = re.search(r"^@code\s*\{", src, re.M)
    return m.start() if m else len(src)

def main(write):
    catalog = {}
    if os.path.exists(CATALOG):
        catalog = {k: v for k, v in json.load(open(CATALOG, encoding="utf-8")).items() if not k.startswith("_")}
    by_text = {v: k for k, v in catalog.items()}
    mixed, changed_files, added = [], 0, 0

    def key_for(path, text):
        nonlocal added
        if text in by_text:
            return by_text[text]
        base = f"{prefix(path)}.{slug(text)}"
        key, n = base, 2
        while key in catalog:
            key, n = f"{base}_{n}", n + 1
        catalog[key] = text
        by_text[text] = key
        added += 1
        return key

    for dirpath, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj", "wwwroot") and d not in SKIP_DIRS]
        for f in sorted(files):
            if not f.endswith(".razor") or f in SKIP_FILES:
                continue
            path = os.path.join(dirpath, f)
            src = open(path, encoding="utf-8").read()
            end = markup_end(src)
            markup, code = src[:end], src[end:]

            def attr_sub(m):
                name, val = m.group(1), m.group(2)
                text = html.unescape(val).strip()
                if text in BRAND or not WORD.search(text):
                    return m.group(0)
                return f'{name}="@L.T("{key_for(path, text)}")"'
            new = ATTR_RE.sub(attr_sub, markup)

            def text_sub(m):
                raw = m.group(1)
                if not WORD.search(raw):
                    return m.group(0)
                # Inside a C# expression or code line, or mixed with @expressions: leave for hand editing.
                code_like = any(t in raw for t in ('"', "&&", "||", "==", "!=", "?.", "=>", ";", "{", "}"))
                if "@" in raw or code_like:
                    if "@" in raw and WORD.search(re.sub(r"@\(?[\w.?!\[\]\"(), ]+\)?", "", raw)):
                        mixed.append((path, raw.strip()[:120]))
                    return m.group(0)
                text = html.unescape(raw).strip()
                if text in BRAND or text.startswith("//"):
                    return m.group(0)
                lead = raw[: len(raw) - len(raw.lstrip())]
                trail = raw[len(raw.rstrip()):]
                return f'>{lead}@L.T("{key_for(path, text)}"){trail}<'
            new = TEXT_RE.sub(text_sub, new)
            if new != markup:
                changed_files += 1
                if write:
                    open(path, "w", encoding="utf-8").write(new + code)

    print(f"{added} new strings, {len(catalog)} in catalog, {changed_files} files changed, {len(mixed)} mixed segments")
    for p, t in mixed:
        print(f"  MIXED {os.path.relpath(p, ROOT)}: {t}")
    if write:
        os.makedirs(os.path.dirname(CATALOG), exist_ok=True)
        ordered = dict(sorted(catalog.items()))
        json.dump(ordered, open(CATALOG, "w", encoding="utf-8"), ensure_ascii=False, indent=1)

if __name__ == "__main__":
    main("--write" in sys.argv)
