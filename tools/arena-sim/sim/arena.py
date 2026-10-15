"""Arena (health) mode simulator for TTT Ultimate.

Base rules (match docs/ARCHITECTURE.md hand-verified cases):
  * NxN board, K-in-a-row (default 3x3, K=3).
  * Global FIFO of placed pieces, capacity W. When the queue is full, placing first
    removes the oldest piece; that vacated cell is a legal target ("vacated playable").
  * Ghost rule: after the placement, if the queue is full (== W) and its front piece
    (the next to vanish) belongs to the mover, that piece cannot count toward a line.
Arena additions:
  * A completed (counted) line does not end the game: all pieces of all counted lines
    SHATTER (removed from board and queue, queue order of the rest preserved) and deal
    damage to the opponent.
  * damage = nlines * ramp[min(streak, len(ramp)) - 1] + (crit_bonus if nlines >= 2)
    where streak = number of consecutive own turns that scored (incl. this one).
  * KO at hp <= 0 ends immediately. Clock handled in play().

State is an immutable tuple: (xbits, obits, queue_tuple, hpX, hpO, streakX, streakO, mover)
mover 0 = X, 1 = O.
"""
import random

INF = 10 ** 9
WIN = 100000


class Rules:
    def __init__(self, n=3, k=3, W=7, ghost=True, hp=100, ramp=(25, 35, 50),
                 crit_bonus=15, vacated=True, combo="consecutive", knockback=0):
        # combo: "consecutive" = scored on each of your consecutive own turns (spec);
        #        "unanswered" = your lines in a row with no opponent line in between
        #        (own blank turns do not reset; an opponent line resets it).
        # knockback: on scoring, also remove the victim's k oldest pieces.
        self.combo, self.knockback = combo, knockback
        self.n, self.k, self.W, self.ghost = n, k, W, ghost
        self.hp, self.ramp, self.crit_bonus, self.vacated = hp, tuple(ramp), crit_bonus, vacated
        self.cells = n * n
        self.full = (1 << self.cells) - 1
        lines = []
        for r in range(n):
            for c in range(n):
                for dr, dc in ((0, 1), (1, 0), (1, 1), (1, -1)):
                    cells = [(r + i * dr, c + i * dc) for i in range(k)]
                    if all(0 <= rr < n and 0 <= cc < n for rr, cc in cells):
                        m = 0
                        for rr, cc in cells:
                            m |= 1 << (rr * n + cc)
                        lines.append(m)
        self.lines = lines
        self.through = [[L for L in lines if (L >> c) & 1] for c in range(self.cells)]
        # for threat eval: (line, list of cells)
        self.line_cells = [(L, [c for c in range(self.cells) if (L >> c) & 1]) for L in lines]

    def key(self):
        return dict(n=self.n, k=self.k, W=self.W, ghost=int(self.ghost), hp=self.hp,
                    ramp="-".join(map(str, self.ramp)), crit_bonus=self.crit_bonus,
                    vacated=int(self.vacated), combo=self.combo, knockback=self.knockback)

    def start(self, hp=None):
        h = self.hp if hp is None else hp
        return (0, 0, (), h, h, 0, 0, 0)


def legal(R, s):
    x, o, q = s[0], s[1], s[2]
    empty = R.full & ~(x | o)
    moves = [c for c in range(R.cells) if (empty >> c) & 1]
    if R.vacated and len(q) == R.W:
        moves.append(q[0])
    return moves


def popcount(v):
    return bin(v).count("1")


def apply(R, s, c):
    """Return (new_state, nlines, damage, shattered_mask)."""
    x, o, q, h0, h1, s0, s1, m = s
    if len(q) == R.W:
        f = q[0]
        b = ~(1 << f)
        x &= b
        o &= b
        q = q[1:]
    bit = 1 << c
    assert not ((x | o) & bit), "occupied"
    if m == 0:
        x |= bit
        mine = x
    else:
        o |= bit
        mine = o
    q = q + (c,)
    gb = 0
    if R.ghost and len(q) == R.W and (mine >> q[0]) & 1:
        gb = 1 << q[0]
    u = 0
    n = 0
    for L in R.through[c]:
        if mine & L == L and not (L & gb):
            u |= L
            n += 1
    dmg = 0
    if n:
        nu = ~u
        if m == 0:
            x &= nu
        else:
            o &= nu
        if R.knockback:
            theirs = o if m == 0 else x
            kb = 0
            k = R.knockback
            for i in q:
                if k and (theirs >> i) & 1 and not (u >> i) & 1:
                    kb |= 1 << i
                    k -= 1
            if kb:
                u2 = ~kb
                x &= u2
                o &= u2
            u |= kb
        q = tuple(i for i in q if not (u >> i) & 1)
        st = (s0 if m == 0 else s1) + 1
        if R.combo == "unanswered":
            if m == 0:
                s1 = 0
            else:
                s0 = 0
        ramp = R.ramp
        dmg = n * ramp[min(st, len(ramp)) - 1] + (R.crit_bonus if n >= 2 else 0)
        if m == 0:
            h1 -= dmg
            s0 = st
        else:
            h0 -= dmg
            s1 = st
    elif R.combo == "consecutive":
        if m == 0:
            s0 = 0
        else:
            s1 = 0
    return (x, o, q, h0, h1, s0, s1, 1 - m), n, dmg, u


def threats(R, s, p):
    """Number of lines where player p has K-1 pieces and the last cell is empty."""
    mine = s[0] if p == 0 else s[1]
    occ = s[0] | s[1]
    t = 0
    k1 = R.k - 1
    for L, cells in R.line_cells:
        if not (L & occ & ~mine) and popcount(mine & L) == k1:
            t += 1
    return t


def evaluate(R, s, p, tw):
    """Static value for player p."""
    hp_me, hp_op = (s[3], s[4]) if p == 0 else (s[4], s[3])
    v = hp_me - hp_op
    if tw:
        v += tw * (threats(R, s, p) - threats(R, s, 1 - p))
    return v


def negamax(R, s, depth, alpha, beta, tw):
    """Value for side to move in s."""
    me = s[7]
    best = -INF
    for c in legal(R, s):
        s2, n, d, _ = apply(R, s, c)
        opp_hp = s2[4] if me == 0 else s2[3]
        if opp_hp <= 0:
            v = WIN + depth
        elif depth <= 1:
            v = evaluate(R, s2, me, tw)
        else:
            v = -negamax(R, s2, depth - 1, -beta, -alpha, tw)
        if v > best:
            best = v
            if v > alpha:
                alpha = v
                if alpha >= beta:
                    break
    return best


# ---------------------------------------------------------------- bots
class Bot:
    """kind: random | greedy | mm (depth d) | turtle.  eps: prob of a random move."""

    def __init__(self, kind="mm", depth=2, eps=0.0, tw=8, name=None, stall=False):
        self.kind, self.depth, self.eps, self.tw, self.stall = kind, depth, eps, tw, stall
        self.name = name or (kind if kind != "mm" else f"mm{depth}") + (f"e{int(eps*100)}" if eps else "")

    def move(self, R, s, rng):
        moves = legal(R, s)
        if self.kind == "random" or (self.eps and rng.random() < self.eps):
            return rng.choice(moves)
        me = s[7]
        if self.kind == "greedy":
            best, bm = -1, []
            for c in moves:
                _, n, d, _ = apply(R, s, c)
                if d > best:
                    best, bm = d, [c]
                elif d == best:
                    bm.append(c)
            return rng.choice(bm)
        if self.kind == "turtle":
            # never sets up, only: take a KO, else minimise opponent's best reply damage+threats
            best, bm = INF, []
            for c in moves:
                s2, n, d, _ = apply(R, s, c)
                opp_hp = s2[4] if me == 0 else s2[3]
                if opp_hp <= 0:
                    return c
                worst = 0
                for c2 in legal(R, s2):
                    _, n2, d2, _ = apply(R, s2, c2)
                    if d2 > worst:
                        worst = d2
                v = worst * 10 + threats(R, s2, 1 - me)
                if v < best:
                    best, bm = v, [c]
                elif v == best:
                    bm.append(c)
            return rng.choice(bm)
        # minimax: evaluate every root child exactly, random tie-break
        rng.shuffle(moves)
        best, bm = -INF, []
        for c in moves:
            s2, n, d, _ = apply(R, s, c)
            opp_hp = s2[4] if me == 0 else s2[3]
            if opp_hp <= 0:
                v = WIN + self.depth
            elif self.depth <= 1:
                v = evaluate(R, s2, me, self.tw)
            else:
                v = -negamax(R, s2, self.depth - 1, -INF, INF, self.tw)
            if v > best:
                best, bm = v, [c]
            elif v == best:
                bm.append(c)
        return rng.choice(bm)


BOTS = {
    "random": lambda: Bot("random"),
    "greedy": lambda: Bot("greedy"),
    "mm1": lambda: Bot("mm", 1),
    "mm2": lambda: Bot("mm", 2),
    "mm3": lambda: Bot("mm", 3),
    "turtle": lambda: Bot("turtle"),
    # human-like noisy players
    "novice": lambda: Bot("greedy", eps=0.4, name="novice"),
    "casual": lambda: Bot("mm", 2, eps=0.3, name="casual"),
    "club": lambda: Bot("mm", 2, eps=0.12, name="club"),
    "strong": lambda: Bot("mm", 3, eps=0.04, name="strong"),
    "mm2stall": lambda: Bot("mm", 2, name="mm2stall", stall=True),
    "clubstall": lambda: Bot("mm", 2, eps=0.12, name="clubstall", stall=True),
}


def get_bot(name):
    return BOTS[name]()


# ---------------------------------------------------------------- game
def play(R, bx, bo, rng, T=120.0, tmin=1.5, tmax=4.5, p_timeout=0.02, limit=5.0,
         overtime=True, ot_cap=40, first_move=None, record=False, red_mult=1.0, red_secs=30.0, ko_reply=False):
    """Play one timed Arena game. bx moves first (seat X). Returns dict of stats."""
    s = R.start()
    bots = (bx, bo)
    t = 0.0
    moves = [0, 0]
    lines = [0, 0]
    crits = [0, 0]
    dmg_total = [0, 0]
    max_streak = [0, 0]
    first_blood = None
    min_diff = 0   # min of hpX-hpO
    max_diff = 0
    lead = 0
    lead_changes = 0
    occ_sum = 0
    occ_n = 0
    empty_after_shatter = 0
    timeouts = 0
    ot = False
    ot_plies = 0
    result = None
    end = None
    trace = [] if record else None
    ply = 0
    replying = False
    while True:
        m = s[7]
        bot = bots[m]
        occ_sum += popcount(s[0] | s[1])
        occ_n += 1
        if ply == 0 and first_move is not None:
            c = first_move
            dt = rng.uniform(tmin, tmax)
        elif rng.random() < p_timeout:
            c = rng.choice(legal(R, s))
            dt = limit
            timeouts += 1
        else:
            c = bot.move(R, s, rng)
            if bot.stall and ((s[3] - s[4]) if m == 0 else (s[4] - s[3])) > 0:
                dt = limit - 0.1
            else:
                dt = rng.uniform(tmin, tmax)
        s, n, d, u = apply(R, s, c)
        ply += 1
        t += dt
        if n and red_mult != 1.0 and not ot and t >= T - red_secs:
            extra = int(round(d * (red_mult - 1)))
            d += extra
            s = (s[0], s[1], s[2], s[3] - (extra if m == 1 else 0), s[4] - (extra if m == 0 else 0), s[5], s[6], s[7])
        moves[m] += 1
        if record:
            trace.append((m, c, n, d, s[3], s[4], round(t, 2)))
        if n:
            lines[m] += n
            if n >= 2:
                crits[m] += 1
            dmg_total[m] += d
            st = s[5] if m == 0 else s[6]
            if st > max_streak[m]:
                max_streak[m] = st
            if first_blood is None:
                first_blood = t
            if popcount(s[0] | s[1]) <= 1:
                empty_after_shatter += 1
            if ot:
                result, end = m, "overtime"
                break
        diff = s[3] - s[4]
        min_diff = min(min_diff, diff)
        max_diff = max(max_diff, diff)
        sg = (diff > 0) - (diff < 0)
        if sg and sg != lead:
            if lead:
                lead_changes += 1
            lead = sg
        if replying:
            # O's equalising reply to X's KO: both KO'd -> higher HP wins; equal -> sudden death
            if s[3] > 0 or s[3] > s[4]:
                result, end = 0, "KO"
                break
            if s[4] > s[3]:
                result, end = 1, "KO"
                break
            replying = False
            ot = True
            s = (s[0], s[1], s[2], 1, 1, s[5], s[6], s[7])
            continue
        if s[3] <= 0 or s[4] <= 0:
            if ko_reply and m == 0 and not ot:
                replying = True   # O still gets its equal turn
                continue
            result, end = (0 if s[4] <= 0 else 1), "KO"
            break
        if ot:
            ot_plies += 1
            if ot_plies >= ot_cap:
                result, end = None, "ot_draw"
                break
            continue
        if t >= T and moves[0] == moves[1]:
            if s[3] != s[4]:
                result, end = (0 if s[3] > s[4] else 1), "timeout"
                break
            if not overtime:
                result, end = None, "draw"
                break
            ot = True
            # sudden death: any line wins -> model as 1 HP each for search purposes
            s = (s[0], s[1], s[2], 1, 1, s[5], s[6], s[7])
    hpX, hpO = (s[3], s[4]) if end != "overtime" else (None, None)
    winner_deficit = 0
    if result == 0:
        winner_deficit = -min_diff
    elif result == 1:
        winner_deficit = max_diff
    out = dict(winner=result, end=end, t=round(t, 2), plies=ply, movesX=moves[0], movesO=moves[1],
               linesX=lines[0], linesO=lines[1], critsX=crits[0], critsO=crits[1],
               dmgX=dmg_total[0], dmgO=dmg_total[1], maxStreakX=max_streak[0], maxStreakO=max_streak[1],
               firstBlood=None if first_blood is None else round(first_blood, 2),
               winnerMaxDeficit=winner_deficit, leadChanges=lead_changes,
               avgOcc=round(occ_sum / occ_n, 3), nearEmpty=empty_after_shatter,
               timeouts=timeouts, otPlies=ot_plies, hpX=hpX, hpO=hpO,
               otWinnerX=int(end == "overtime" and result == 0))
    if record:
        out["trace"] = trace
    return out
