# plain 4x4 3-in-a-row (no removal): minimal forced win length (total plies) after each opening
import sys
from functools import lru_cache
L=[]
for r in range(4):
    for c in range(4):
        for dr,dc in((0,1),(1,0),(1,1),(1,-1)):
            cells=[(r+i*dr,c+i*dc) for i in range(3)]
            if all(0<=a<4 and 0<=b<4 for a,b in cells): L.append(sum(1<<(a*4+b) for a,b in cells))
def win(m): return any(m&l==l for l in L)
sys.setrecursionlimit(10000)
@lru_cache(maxsize=None)
def can(me,opp,d):  # side to move (me) can force a win within d own-or-opp plies
    if d<=0: return False
    occ=me|opp
    for e in range(16):
        if occ>>e&1: continue
        m=me|1<<e
        if win(m): return True
    if d<3: return False
    for e in range(16):
        if occ>>e&1: continue
        m=me|1<<e
        ok=True
        for f in range(16):
            if (occ|1<<e)>>f&1: continue
            o=opp|1<<f
            if win(o) or not can(m,o,d-2): ok=False;break
        if ok and (occ|1<<e)!=0xFFFF: return True
    return False
for o in (0,1,5):
    for d in range(1,16,2):
        # after opening o, O to move; X wins if O cannot avoid: check "O loses within d"
        # X = opp. X wins within d plies after opening iff for all O replies f, X can win within d-1
        ok=True
        for f in range(16):
            if f==o: continue
            if not can(1<<o, 1<<f, d-1): ok=False;break
        if ok: print("opening",o,"X forces win, total plies",1+d); break
    else: print("opening",o,"no forced win within 16 plies")
