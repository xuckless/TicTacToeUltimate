#!/usr/bin/env python3
"""Folds wwwroot/i18n/partials/*.json into en.json and checks the catalog against the code.

  python3 tools/i18n/merge.py            # merge partials, report missing / unused keys
  python3 tools/i18n/merge.py --prune    # also drop unused keys from en.json (and every translation)

Used keys are found as L.T("…"), L.F("…"), L.P("…") (plural: key|one, key|other…) and quoted "x.y" literals that match a
catalog key (keys passed around as data). Interpolated keys like L.T($"tier.{t}") protect every key with that prefix.
"""
import glob, json, os, re, sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
I18N = os.path.join(ROOT, "TicTacToeUltimate.Shared", "wwwroot", "i18n")
EN = os.path.join(I18N, "en.json")
CODE_DIRS = ["TicTacToeUltimate.Shared", "TicTacToeUltimate.Client"]

def load(p):
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}

def main(prune):
    en = load(EN)
    for p in sorted(glob.glob(os.path.join(I18N, "partials", "*.json"))):
        for k, v in load(p).items():
            if k in en and en[k] != v:
                print(f"CONFLICT {k}: {en[k]!r} vs {v!r} ({os.path.basename(p)})")
            en.setdefault(k, v)
    code = ""
    for d in CODE_DIRS:
        for p in glob.glob(os.path.join(ROOT, d, "**", "*.*"), recursive=True):
            if p.endswith((".razor", ".cs")) and "/bin/" not in p and "/obj/" not in p:
                code += open(p, encoding="utf-8").read() + "\n"
    code = re.sub(r"///.*", "", code)   # doc-comment examples are not uses
    used = set(re.findall(r'L\.[TFP]\(\s*"([^"]+)"', code))
    used |= {k for k in en if f'"{k}"' in code}
    prefixes = set(re.findall(r'L\.[TFP]\(\s*\$"([^"{]+)\{', code)) | set(re.findall(r'Current\[\s*\$"([^"{]+)\{', code)) | set(re.findall(r'\$"([a-zA-Z]+\.[a-zA-Z_.]*)\{', code))
    prefixes |= set(re.findall(r'"([a-zA-Z]+\.)"\s*\+', code))   # "tier." + name
    def is_used(k):
        base = k.split("|")[0]
        return base in used or any(base.startswith(p) for p in prefixes)
    missing = sorted(k for k in re.findall(r'L\.[TF]\(\s*"([^"]+)"', code) if k not in en)
    missing += sorted(k for k in re.findall(r'L\.P\(\s*"([^"]+)"', code) if f"{k}|other" not in en)
    unused = sorted(k for k in en if not is_used(k))
    print(f"{len(en)} keys, {len(set(missing))} missing, {len(unused)} unused")
    for k in sorted(set(missing)): print("  MISSING", k)
    for k in unused: print("  unused", k, "=", repr(en[k])[:60])
    if prune:
        for k in unused: en.pop(k)
        for p in glob.glob(os.path.join(I18N, "*.json")):
            if os.path.basename(p) == "en.json": continue
            t = load(p)
            for k in unused: t.pop(k, None)
            json.dump(t, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=1, sort_keys=True)
    json.dump(dict(sorted(en.items())), open(EN, "w", encoding="utf-8"), ensure_ascii=False, indent=1)

if __name__ == "__main__":
    main("--prune" in sys.argv)
