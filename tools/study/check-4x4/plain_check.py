# Plain 4x4 3-in-a-row, no removal: minimal plies (total from empty) for X to force a win after each opening.
from functools import lru_cache
L=[]
for r in range(4):
    for c in range(4):
        for dr,dc in ((0,1),(1,0),(1,1),(1,-1)):
            cells=[(r+dr*k,c+dc*k) for k in range(3)]
            if all(0<=a<4 and 0<=b<4 for a,b in cells): L.append(sum(1<<(a*4+b) for a,b in cells))
def won(m): return any(m&l==l for l in L)
@lru_cache(None)
def win(me,op,d):  # can side to move (me) force a win within d of its own-or-opp plies
    if d<=0: return False
    free=[c for c in range(16) if not (me|op)>>c&1]
    for c in free:
        if won(me|1<<c): return True
    if d<3: return False
    for c in free:
        m2=me|1<<c
        if all(not won(op|1<<e) and win(m2,op|1<<e,d-2) for e in range(16) if not (m2|op)>>e&1 and True) and any(not (m2|op)>>e&1 for e in range(16)):
            return True
    return False
for o in (0,1,5):
    d=1
    while not win(0,1<<o,d+1) if False else True:
        # after X opens at o, O to move; X wins in d plies after opening if every O reply leaves X win within d-1
        ok=all(win(1<<o,1<<e,d-1) for e in range(16) if e!=o)
        if ok: break
        d+=2
    print("open",o,"X wins in",d,"plies after opening, total",d+1)
