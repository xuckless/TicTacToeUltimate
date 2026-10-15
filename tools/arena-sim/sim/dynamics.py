"""Trace-level dynamics on the baseline ruleset: trade-backs, combos, tie causes, board after shatter."""
import sys, os, json, random, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from arena import Rules, play, get_bot, apply, legal, popcount
from experiments import BASE, CLOCK
out = {}
for pair in [("casual","casual"),("club","club"),("strong","strong")]:
    R = Rules(**BASE); rng = random.Random(77)
    A, B = get_bot(pair[0]), get_bot(pair[1])
    lines_ev = 0; tradeback1 = 0; repeat_own = 0; tie_at_120 = 0; diff_hist = collections.Counter()
    streak2 = 0; games = 2000; reply_had_line_available = 0
    for g in range(games):
        r = play(R, A, B, rng, record=True, **CLOCK)
        tr = r["trace"]
        for i,(m,c,n,d,hx,ho,t) in enumerate(tr):
            if n:
                lines_ev += 1
                if i+1 < len(tr) and tr[i+1][2]: tradeback1 += 1
                if i+2 < len(tr) and tr[i+2][2] and tr[i+2][0]==m: repeat_own += 1
        if r["end"] in ("overtime","ot_draw"): tie_at_120 += 1
        if max(r["maxStreakX"], r["maxStreakO"]) >= 2: streak2 += 1
        # hp diff at regulation end (last trace entry with t>=T and equal moves) approximated: final non-OT
        if r["end"] == "timeout": diff_hist[abs(r["hpX"]-r["hpO"])] += 1
    out["-".join(pair)] = dict(scoringMoves=lines_ev, P_oppScoresImmediately=tradeback1/lines_ev,
        P_sameScorerNextOwnTurn=repeat_own/lines_ev, gamesWithStreak2=streak2/games, tiedAtTime=tie_at_120/games,
        timeoutMarginHist=dict(sorted(diff_hist.items())))
# board right after a shatter: how many pieces of each side remain (baseline, club-club)
R = Rules(**BASE); rng = random.Random(5); A=get_bot("club")
after = collections.Counter(); scorer_vs_opp = [0,0]; n=0
for g in range(1500):
    s = R.start(); t=0
    for ply in range(60):
        m = s[7]; c = A.move(R, s, rng)
        s, nl, d, u = apply(R, s, c)
        if nl:
            mine = s[0] if m==0 else s[1]; opp = s[1] if m==0 else s[0]
            after[popcount(s[0]|s[1])] += 1; scorer_vs_opp[0]+=popcount(mine); scorer_vs_opp[1]+=popcount(opp); n+=1
out["afterShatter_club"] = dict(piecesLeftHist=dict(sorted(after.items())), avgScorerPieces=scorer_vs_opp[0]/n, avgVictimPieces=scorer_vs_opp[1]/n)
json.dump(out, open(os.path.join(os.path.dirname(os.path.abspath(__file__)),"results","dynamics.json"),"w"), indent=1)
print(json.dumps(out, indent=1))
