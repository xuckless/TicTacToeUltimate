#!/usr/bin/env python3
"""Lists C# string literals that look like UI text (Shared .razor/.cs, Client user-facing .cs) still not in the catalog."""
import os, re, sys
ROOTS = ["TicTacToeUltimate.Shared", "TicTacToeUltimate.Client"]
SKIP = {"Icon.razor", "Bert.razor", "PieceBuddy.razor", "Wallpaper.razor", "BertFlyby.razor", "_Imports.razor", "Localizer.cs"}
LIT = re.compile(r'\$?"((?:[^"\\\n]|\\.)*)"')
def ui_like(s):
    t = re.sub(r"\{[^}]*\}", "", s)
    if not re.search(r"[A-Za-z]{2,}", t): return False
    if re.fullmatch(r"[a-z0-9_\-./:#?=&%]+", t): return False          # ids, routes, css, keys
    if re.fullmatch(r"[A-Za-z]+\.[A-Za-z.]+", t): return False         # Type.Member
    if t.startswith(("ttt.", "api/", "http", "_content", "data:", "bert:", "emote:", "image/", "application/", "#", "--")): return False
    if re.fullmatch(r"[a-z][a-zA-Z]+", t): return False                 # jsFunction names
    return " " in t.strip() or t[:1].isupper()
count = 0
for root in ROOTS:
    for dp, ds, fs in os.walk(root):
        ds[:] = [d for d in ds if d not in ("bin", "obj", "wwwroot", "Fakes")]
        for f in sorted(fs):
            if f in SKIP or not f.endswith((".razor", ".cs")): continue
            p = os.path.join(dp, f)
            for i, line in enumerate(open(p, encoding="utf-8"), 1):
                s = line.strip()
                if s.startswith(("//", "///", "@*", "*", "[LoggerMessage", "_logger.", "log.", "Log.", "logger.")) or "Log(" in s and "Log" in s[:20]: continue
                if "L.T(" in line and line.count('"') <= 2: continue
                for m in LIT.finditer(line):
                    if ui_like(m.group(1)) and "LogWarning" not in line and "LogInformation" not in line and "LogDebug" not in line and "LogError" not in line and "InvokeAsync" not in line and "InvokeVoidAsync" not in line and "nameof" not in line:
                        count += 1
                        print(f"{p}:{i}: {m.group(0)[:100]}")
print(count, file=sys.stderr)
