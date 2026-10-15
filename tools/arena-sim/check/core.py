"""Independent Arena re-implementation (list-based, no import from ../sim)."""
import random
LINES = [(0,1,2),(3,4,5),(6,7,8),(0,3,6),(1,4,7),(2,5,8),(0,4,8),(2,4,6)]
BY_CELL = {c: [L for L in LINES if c in L] for c in range(9)}

class G:
    __slots__ = ("b","q","hp","st","turn")
    def __init__(s, hp=100):
        s.b = [None]*9; s.q = []; s.hp = [hp, hp]; s.st = [0, 0]; s.turn = 0
    def copy(s):
        g = G.__new__(G); g.b = s.b[:]; g.q = s.q[:]; g.hp = s.hp[:]; g.st = s.st[:]; g.turn = s.turn; return g

def moves(g, W):
    m = [c for c in range(9) if g.b[c] is None]
    if len(g.q) == W: m.append(g.q[0])
    return m

def play_move(g, c, cfg):
    """mutates g. returns (nlines, damage)."""
    W = cfg["W"]; p = g.turn
    if len(g.q) == W:
        old = g.q.pop(0); g.b[old] = None
    assert g.b[c] is None
    g.b[c] = p; g.q.append(c)
    ghost = g.q[0] if (cfg["ghost"] and len(g.q) == W and g.b[g.q[0]] == p) else None
    done = [L for L in BY_CELL[c] if all(g.b[i] == p for i in L) and ghost not in L]
    dmg = 0
    if done:
        cells = set(i for L in done for i in L)
        for i in cells: g.b[i] = None
        g.q = [i for i in g.q if i not in cells]
        g.st[p] += 1
        if cfg["combo"] == "unanswered": g.st[1-p] = 0
        ramp = cfg["ramp"]
        dmg = len(done) * ramp[min(g.st[p], len(ramp)) - 1] + (cfg["crit"] if len(done) > 1 else 0)
        g.hp[1-p] -= dmg
    elif cfg["combo"] == "consecutive":
        g.st[p] = 0
    g.turn = 1 - p
    return len(done), dmg

def threats(g, p):
    t = 0
    for L in LINES:
        v = [g.b[i] for i in L]
        if v.count(p) == 2 and v.count(None) == 1: t += 1
    return t

def search(g, depth, cfg, sudden):
    """negamax value for side to move."""
    p = g.turn; best = -10**9
    for c in moves(g, cfg["W"]):
        h = g.copy(); n, d = play_move(h, c, cfg)
        if (sudden and n) or h.hp[1-p] <= 0:
            v = 10**6 + depth
        elif depth == 1:
            v = (h.hp[p] - h.hp[1-p]) + 8 * (threats(h, p) - threats(h, 1-p))
            if sudden: v = 8 * (threats(h, p) - threats(h, 1-p))
        else:
            v = -search(h, depth-1, cfg, sudden)
        if v > best: best = v
    return best

def choose(kind, g, cfg, rng, sudden):
    ms = moves(g, cfg["W"])
    k, depth, eps = kind
    if k == "random" or rng.random() < eps: return rng.choice(ms)
    p = g.turn
    scored = []
    for c in ms:
        h = g.copy(); n, d = play_move(h, c, cfg)
        if k == "greedy":
            v = d
        elif (sudden and n) or h.hp[1-p] <= 0:
            v = 10**6 + depth
        elif depth == 1:
            v = (h.hp[p]-h.hp[1-p]) + 8*(threats(h,p)-threats(h,1-p))
        else:
            v = -search(h, depth-1, cfg, sudden)
        scored.append((v, c))
    bv = max(v for v, _ in scored)
    return rng.choice([c for v, c in scored if v == bv])

KINDS = {"random":("random",0,0),"greedy":("greedy",0,0),"novice":("greedy",0,.4),
         "casual":("mm",2,.3),"club":("mm",2,.12),"strong":("mm",3,.04),"mm2":("mm",2,0)}

def game(kx, ko, cfg, rng, T=120.0, red=1.0, redsecs=30.0):
    g = G(cfg["hp"]); t = 0.0; cnt = [0,0]; sudden = False; ply_ot = 0; kinds = (KINDS[kx], KINDS[ko])
    crit = False
    while True:
        p = g.turn
        if rng.random() < 0.02:
            c = rng.choice(moves(g, cfg["W"])); dt = 5.0
        else:
            c = choose(kinds[p], g, cfg, rng, sudden); dt = rng.uniform(1.5, 4.5)
        n, d = play_move(g, c, cfg); t += dt; cnt[p] += 1
        if n > 1: crit = True
        if n and sudden: return dict(w=p, end="ot", crit=crit)
        if n and red != 1.0 and not sudden and t >= T - redsecs:
            g.hp[1-p] -= int(round(d*(red-1)))
        if g.hp[1-p] <= 0: return dict(w=p, end="ko", crit=crit)
        if sudden:
            ply_ot += 1
            if ply_ot >= 40: return dict(w=None, end="otdraw", crit=crit)
            continue
        if t >= T and cnt[0] == cnt[1]:
            if g.hp[0] != g.hp[1]: return dict(w=0 if g.hp[0] > g.hp[1] else 1, end="time", crit=crit)
            sudden = True
