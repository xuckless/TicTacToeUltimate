"""Run Arena experiments in parallel. Usage: python3 -I experiments.py <name> [...]
Writes raw per-game CSV and aggregated JSON into results/."""
import csv
import json
import os
import random
import sys
import time
from multiprocessing import Pool

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from arena import Rules, play, get_bot  # noqa: E402

RES = os.path.join(HERE, "results")

BASE = dict(n=3, k=3, W=7, ghost=True, hp=100, ramp=(25, 35, 50), crit_bonus=15)
CLOCK = dict(T=120.0, tmin=1.5, tmax=4.5, p_timeout=0.02)


def ramp_of(D, kind):
    if kind == "flat":
        return (D,)
    if kind == "spec":  # 25->35->50 shape
        return (D, round(D * 1.4), D * 2)
    if kind == "steep":
        return (D, D * 2, D * 3)
    raise ValueError(kind)


def task(args):
    tag, rules, clock, bx, bo, n, seed, extra = args
    R = Rules(**rules)
    rng = random.Random(seed)
    rows = []
    BX, BO = get_bot(bx), get_bot(bo)
    for i in range(n):
        r = play(R, BX, BO, rng, **clock, **extra)
        r.pop("trace", None)
        row = dict(tag=tag, botX=bx, botO=bo, **R.key(), T=clock["T"])
        row.update({k: v for k, v in extra.items()})
        row.update(r)
        rows.append(row)
    return rows


def run_tasks(tasks, chunk=250):
    # split big tasks into chunks for load balance
    split = []
    for (tag, rules, clock, bx, bo, n, seed, extra) in tasks:
        k = 0
        while n > 0:
            m = min(chunk, n)
            split.append((tag, rules, clock, bx, bo, m, seed * 1000 + k, extra))
            n -= m
            k += 1
    with Pool(10) as p:
        out = []
        for rows in p.imap_unordered(task, split):
            out.extend(rows)
    return out


def write_csv(name, rows):
    path = os.path.join(RES, name + ".csv")
    keys = []
    for r in rows:
        for k in r:
            if k not in keys:
                keys.append(k)
    with open(path, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=keys)
        w.writeheader()
        w.writerows(rows)
    return path


def agg(rows, strong=None):
    """Aggregate a list of game rows. strong: name of the stronger bot (for skill win rate)."""
    N = len(rows)
    if not N:
        return {}
    c = lambda f: sum(1 for r in rows if f(r))
    lines = sum(r["linesX"] + r["linesO"] for r in rows)
    minutes = sum(r["t"] for r in rows) / 60
    fb = [r["firstBlood"] for r in rows if r["firstBlood"] is not None]
    fb.sort()
    D = None
    out = dict(
        games=N,
        xWin=c(lambda r: r["winner"] == 0) / N,
        oWin=c(lambda r: r["winner"] == 1) / N,
        draw=c(lambda r: r["winner"] is None) / N,
        KO=c(lambda r: r["end"] == "KO") / N,
        timeout=c(lambda r: r["end"] == "timeout") / N,
        overtime=c(lambda r: r["end"] == "overtime") / N,
        otDraw=c(lambda r: r["end"] in ("ot_draw", "draw")) / N,
        linesPerGame=lines / N,
        linesPerMin=lines / minutes if minutes else 0,
        plies=sum(r["plies"] for r in rows) / N,
        gameSecs=sum(r["t"] for r in rows) / N,
        firstBloodMedian=fb[len(fb) // 2] if fb else None,
        noBlood=c(lambda r: r["firstBlood"] is None) / N,
        critsPerGame=sum(r["critsX"] + r["critsO"] for r in rows) / N,
        gamesWithCrit=c(lambda r: r["critsX"] + r["critsO"] > 0) / N,
        critShareOfLines=(sum(r["critsX"] + r["critsO"] for r in rows) / lines) if lines else 0,
        streak3=c(lambda r: max(r["maxStreakX"], r["maxStreakO"]) >= 3) / N,
        maxStreak=max(max(r["maxStreakX"], r["maxStreakO"]) for r in rows),
        # comeback: winner trailed by at least one base line of damage at some point
        comeback=c(lambda r: r["winner"] is not None and float(r["winnerMaxDeficit"]) >= float(str(r["ramp"]).split("-")[0])) / N,
        comeback2=c(lambda r: r["winner"] is not None and float(r["winnerMaxDeficit"]) >= 2 * float(str(r["ramp"]).split("-")[0])) / N,
        otXShare=(sum(int(r.get("otWinnerX", 0)) for r in rows) / max(1, c(lambda r: r["end"] == "overtime"))),
        otPliesMean=(sum(float(r["otPlies"]) for r in rows if r["end"] in ("overtime", "ot_draw")) / max(1, c(lambda r: r["end"] in ("overtime", "ot_draw")))),
        comebackAny=c(lambda r: r["winner"] is not None and r["winnerMaxDeficit"] > 0) / N,
        leadChanges=sum(r["leadChanges"] for r in rows) / N,
        avgOcc=sum(r["avgOcc"] for r in rows) / N,
        nearEmptyPerGame=sum(r["nearEmpty"] for r in rows) / N,
    )
    if strong:
        w = 0
        dec = 0
        for r in rows:
            if r["winner"] is None:
                continue
            dec += 1
            sb = r["botX"] if r["winner"] == 0 else r["botO"]
            if sb == strong:
                w += 1
        out["strongWin"] = w / N
        out["strongWinDecided"] = w / dec if dec else None
    return out


def both_seats(tag, rules, a, b, n, seed, clock=CLOCK, extra=None):
    extra = extra or {}
    return [(tag, rules, clock, a, b, n // 2, seed, extra), (tag, rules, clock, b, a, n - n // 2, seed + 1, extra)]


# --------------------------------------------------------------------------- experiments
def exp_pace():
    pairs = [("random", "random"), ("greedy", "greedy"), ("novice", "novice"), ("casual", "casual"),
             ("club", "club"), ("strong", "strong"), ("mm2", "mm2"), ("mm3", "mm3"),
             ("club", "casual"), ("strong", "casual"), ("strong", "club")]
    tasks = []
    for i, (a, b) in enumerate(pairs):
        tasks += both_seats(f"{a}-{b}", BASE, a, b, 3000 if "mm3" not in (a, b) else 1500, 100 + 2 * i)
    rows = run_tasks(tasks)
    write_csv("pace_raw", rows)
    out = {}
    for (a, b) in pairs:
        rs = [r for r in rows if r["tag"] == f"{a}-{b}"]
        out[f"{a}-{b}"] = agg(rs, strong=a if a != b else None)
    return out


BOTSET = ["random", "greedy", "mm1", "mm2", "mm3", "novice", "casual", "club", "strong"]


def exp_fair():
    tasks = []
    s = 5000
    for a in BOTSET:
        for b in BOTSET:
            n = 600 if ("mm3" in (a, b) or "strong" in (a, b)) else 1000
            tasks.append((f"{a}|{b}", BASE, CLOCK, a, b, n, s, {}))
            s += 1
    rows = run_tasks(tasks)
    write_csv("fair_raw", rows)
    mat = {}
    for a in BOTSET:
        for b in BOTSET:
            rs = [r for r in rows if r["botX"] == a and r["botO"] == b]
            g = agg(rs)
            mat[f"{a}|{b}"] = dict(xWin=g["xWin"], oWin=g["oWin"], draw=g["draw"], games=g["games"],
                                   KO=g["KO"], overtime=g["overtime"])
    return mat


def exp_pie():
    """Per-opening X win rate for equal bots: what pie would neutralise."""
    tasks = []
    s = 9000
    for bot in ["casual", "club", "strong", "mm2"]:
        for cell in (4, 0, 1):
            n = 3000 if bot not in ("strong",) else 1500
            tasks.append((f"{bot}@{cell}", BASE, CLOCK, bot, bot, n, s, {"first_move": cell}))
            s += 1
    rows = run_tasks(tasks)
    write_csv("pie_raw", rows)
    out = {}
    for bot in ["casual", "club", "strong", "mm2"]:
        out[bot] = {}
        for cell in (4, 0, 1):
            g = agg([r for r in rows if r["tag"] == f"{bot}@{cell}"])
            out[bot][{4: "centre", 0: "corner", 1: "edge"}[cell]] = dict(
                xWin=g["xWin"], oWin=g["oWin"], draw=g["draw"], xScore=g["xWin"] + g["draw"] / 2)
    return out


BOARDS = {
    "3x3 W7 ghost": dict(BASE),
    "3x3 W7 noghost": dict(BASE, ghost=False),
    "3x3 W5 ghost": dict(BASE, W=5),
    "3x3 W8 ghost": dict(BASE, W=8),
    "3x3 W9 ghost": dict(BASE, W=9),
    "3x3 W9 noghost": dict(BASE, W=9, ghost=False),
    "4x4k3 W7 ghost": dict(BASE, n=4, W=7),
    "4x4k3 W8 ghost": dict(BASE, n=4, W=8),
    "4x4k3 W9 ghost": dict(BASE, n=4, W=9),
    "4x4k3 W9 noghost": dict(BASE, n=4, W=9, ghost=False),
}


def exp_crit():
    tasks = []
    s = 12000
    for name, rules in BOARDS.items():
        for a, b in (("casual", "casual"), ("club", "club"), ("random", "random"), ("club", "casual")):
            n = 2000
            tasks += both_seats(f"{name}|{a}-{b}", rules, a, b, n, s)
            s += 2
    rows = run_tasks(tasks)
    write_csv("crit_raw", rows)
    out = {}
    for name in BOARDS:
        out[name] = {}
        for a, b in (("casual", "casual"), ("club", "club"), ("random", "random"), ("club", "casual")):
            out[name][f"{a}-{b}"] = agg([r for r in rows if r["tag"] == f"{name}|{a}-{b}"],
                                        strong=a if a != b else None)
    return out


def exp_degen():
    pairs = [("turtle", "turtle"), ("turtle", "club"), ("turtle", "strong"), ("turtle", "casual"),
             ("clubstall", "club"), ("club", "club"), ("clubstall", "casual"), ("club", "casual"),
             ("mm2stall", "mm2"), ("mm2", "mm2")]
    tasks = []
    s = 15000
    for a, b in pairs:
        tasks += both_seats(f"{a}-{b}", BASE, a, b, 2000, s)
        s += 2
    # no-shatter reference board occupancy: hp 1e6 no damage -> not meaningful; use ghost base rules
    # with lines not removed is a different game; we report occupancy in Arena only.
    rows = run_tasks(tasks)
    write_csv("degen_raw", rows)
    out = {}
    for a, b in pairs:
        out[f"{a}-{b}"] = agg([r for r in rows if r["tag"] == f"{a}-{b}"], strong=a if a != b else None)
    return out


def sweep_configs():
    cfgs = []
    for D in (20, 25, 34):
        for rk in ("flat", "spec", "steep"):
            for hp in (100, 150):
                for T in (90.0, 120.0):
                    for W in (5, 7, 9):
                        for g in (True, False):
                            cfgs.append((dict(BASE, W=W, ghost=g, hp=hp, ramp=ramp_of(D, rk)), T, D, rk))
    return cfgs


def exp_sweep(n=800):
    tasks = []
    s = 20000
    cfgs = sweep_configs()
    for i, (rules, T, D, rk) in enumerate(cfgs):
        clock = dict(CLOCK, T=T)
        tag = f"c{i}"
        tasks += both_seats(tag + "|eq", rules, "club", "club", n, s, clock)
        tasks += both_seats(tag + "|eqc", rules, "casual", "casual", n, s + 2, clock)
        tasks += both_seats(tag + "|gap", rules, "club", "casual", n, s + 4, clock)
        tasks += both_seats(tag + "|gap2", rules, "strong", "club", n // 2, s + 6, clock)
        s += 8
    rows = run_tasks(tasks, chunk=200)
    write_csv("sweep_raw", rows)
    out = []
    by = {}
    for r in rows:
        by.setdefault(r["tag"], []).append(r)
    for i, (rules, T, D, rk) in enumerate(cfgs):
        tag = f"c{i}"
        eq = agg(by[tag + "|eq"])
        eqc = agg(by[tag + "|eqc"])
        gap = agg(by[tag + "|gap"], strong="club")
        gap2 = agg(by[tag + "|gap2"], strong="strong")
        out.append(dict(cfg=i, D=D, ramp=rk, rampVals="-".join(map(str, rules["ramp"])), hp=rules["hp"], T=T,
                        W=rules["W"], ghost=int(rules["ghost"]),
                        KO10_club=10 * eq["KO"], KO10_casual=10 * eqc["KO"],
                        OT_club=eq["overtime"] + eq["otDraw"], OT_casual=eqc["overtime"] + eqc["otDraw"],
                        draw_club=eq["draw"],
                        xEdge_club=eq["xWin"] - eq["oWin"],
                        comeback_club=eq["comeback"], comeback_casual=eqc["comeback"],
                        leadChanges_club=eq["leadChanges"],
                        linesPerMin_club=eq["linesPerMin"], firstBlood_club=eq["firstBloodMedian"],
                        crits_club=eq["critsPerGame"], streak3_club=eq["streak3"],
                        strongWin_clubVsCasual=gap["strongWin"], strongWin_strongVsClub=gap2["strongWin"],
                        KO10_gap=10 * gap["KO"]))
    return out


VARIANTS = {}
for rk in ("spec", "steep"):
    for combo in ("consecutive", "unanswered"):
        for kb in (0, 1):
            for D, hp in ((25, 100), (34, 100)):
                VARIANTS[f"D{D} {rk} {combo} kb{kb}"] = dict(BASE, ramp=ramp_of(D, rk), hp=hp, combo=combo, knockback=kb)


def exp_variants(n=1600):
    tasks = []
    s = 40000
    pairs = (("club", "club"), ("casual", "casual"), ("club", "casual"), ("strong", "club"), ("strong", "strong"))
    for name, rules in VARIANTS.items():
        for a, b in pairs:
            tasks += both_seats(f"{name}|{a}-{b}", rules, a, b, n if a != "strong" else n // 2, s)
            s += 2
    rows = run_tasks(tasks)
    write_csv("variants_raw", rows)
    out = {}
    for name in VARIANTS:
        out[name] = {}
        for a, b in pairs:
            out[name][f"{a}-{b}"] = agg([r for r in rows if r["tag"] == f"{name}|{a}-{b}"], strong=a if a != b else None)
    return out


FINAL = {
    "A spec (owner)": (dict(BASE), 120.0, {}),
    "B A+unanswered combo": (dict(BASE, combo="unanswered"), 120.0, {}),
    "C B+red-alert x2": (dict(BASE, combo="unanswered"), 120.0, {"red_mult": 2.0}),
    "D C with steep ramp 25-50-75": (dict(BASE, combo="unanswered", ramp=(25, 50, 75)), 120.0, {"red_mult": 2.0}),
    "E steep unanswered (no red)": (dict(BASE, combo="unanswered", ramp=(25, 50, 75)), 120.0, {}),
    "F C at 90s": (dict(BASE, combo="unanswered"), 90.0, {"red_mult": 2.0}),
    "G A+red-alert x2 only": (dict(BASE), 120.0, {"red_mult": 2.0}),
    "H D34 unanswered red x2": (dict(BASE, combo="unanswered", ramp=(34, 48, 68)), 120.0, {"red_mult": 2.0}),
}


FINAL2 = {
    "A spec (owner)": FINAL["A spec (owner)"],
    "A+KO-reply": (dict(BASE), 120.0, {"ko_reply": True}),
    "C B+red-alert x2": FINAL["C B+red-alert x2"],
    "C+KO-reply": (dict(BASE, combo="unanswered"), 120.0, {"red_mult": 2.0, "ko_reply": True}),
    "D+KO-reply": (dict(BASE, combo="unanswered", ramp=(25, 50, 75)), 120.0, {"red_mult": 2.0, "ko_reply": True}),
    "C+KO-reply HP125": (dict(BASE, combo="unanswered", hp=125), 120.0, {"red_mult": 2.0, "ko_reply": True}),
}


FINAL3 = {
    "B A+unanswered combo": FINAL["B A+unanswered combo"],
    "C B+red-alert x2": FINAL["C B+red-alert x2"],
    "C HP125": (dict(BASE, combo="unanswered", hp=125), 120.0, {"red_mult": 2.0}),
    "C red x1.5 (25->38)": (dict(BASE, combo="unanswered"), 120.0, {"red_mult": 1.5}),
    "C red x1.5 HP125": (dict(BASE, combo="unanswered", hp=125), 120.0, {"red_mult": 1.5}),
}


CAP3 = dict(n=3, k=3, cap=3, hp=125, ramp=(25, 35, 50), crit_bonus=15, combo="unanswered")
FINAL_CAP = {
    "C cap3 (HP125, red x2)": (dict(CAP3), 120.0, {"red_mult": 2.0}),
    "C cap3 HP100": (dict(CAP3, hp=100), 120.0, {"red_mult": 2.0}),
    "C cap3 HP150": (dict(CAP3, hp=150), 120.0, {"red_mult": 2.0}),
    "C cap3 steep 25-50-75": (dict(CAP3, ramp=(25, 50, 75)), 120.0, {"red_mult": 2.0}),
    "B cap3 (HP100, no red)": (dict(CAP3, hp=100), 120.0, {}),
}


def exp_cap3():
    return exp_final(n=1500, table=FINAL_CAP, out="cap3_raw", seed0=90000)


def exp_final3():
    return exp_final(table=FINAL3, out="final3_raw", seed0=80000)


def exp_final2():
    return exp_final(table=FINAL2, out="final2_raw", seed0=70000)


def exp_final(n=4000, table=None, out="final_raw", seed0=60000):
    FINAL_ = table or FINAL
    tasks = []
    s = seed0
    pairs = (("novice", "novice"), ("casual", "casual"), ("club", "club"), ("strong", "strong"),
             ("club", "casual"), ("strong", "club"), ("strong", "casual"), ("casual", "novice"))
    for name, (rules, T, extra) in FINAL_.items():
        clock = dict(CLOCK, T=T)
        for a, b in pairs:
            m = n if "strong" not in (a, b) else n // 2
            tasks += both_seats(f"{name}|{a}-{b}", rules, a, b, m, s, clock, extra)
            s += 2
    rows = run_tasks(tasks)
    write_csv(out, rows)
    out = {}
    for name in FINAL_:
        out[name] = {}
        for a, b in pairs:
            out[name][f"{a}-{b}"] = agg([r for r in rows if r["tag"] == f"{name}|{a}-{b}"], strong=a if a != b else None)
    return out


def main():
    os.makedirs(RES, exist_ok=True)
    for name in sys.argv[1:]:
        t = time.time()
        res = globals()["exp_" + name]()
        with open(os.path.join(RES, name + ".json"), "w") as f:
            json.dump(res, f, indent=1)
        print(name, "done in", round(time.time() - t, 1), "s", flush=True)


if __name__ == "__main__":
    main()
