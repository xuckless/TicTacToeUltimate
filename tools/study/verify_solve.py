import sys
from collections import deque
W = int(sys.argv[1]) if len(sys.argv)>1 else 7
LINES=[(0,1,2),(3,4,5),(6,7,8),(0,3,6),(1,4,7),(2,5,8),(0,4,8),(2,4,6)]
def last_mover_won(q):
    mine=set(q[len(q)-1::-2])
    return any(all(c in mine for c in L) for L in LINES)
def succ(q):
    base = q[1:] if len(q)==W else q
    occ=set(base)
    return [base+(c,) for c in range(9) if c not in occ]
# enumerate reachable
start=(); seen={start:None}; order=[start]; dq=deque([start]); children={}
while dq:
    q=dq.popleft()
    if q and last_mover_won(q): children[q]=[]; continue
    ch=succ(q); children[q]=ch
    for c in ch:
        if c not in seen: seen[c]=None; dq.append(c); order.append(c)
# retrograde: value from perspective of player to move: 'W','L', None=draw/cycle
parents={q:[] for q in seen}
for q,ch in children.items():
    for c in ch: parents[c].append(q)
val={}; cnt={q:len(children[q]) for q in seen}; dist={}
dq=deque()
for q in seen:
    if q and last_mover_won(q): val[q]='L'; dist[q]=0; dq.append(q)
while dq:
    c=dq.popleft()
    for p in parents[c]:
        if p in val: continue
        if val[c]=='L': val[p]='W'; dist[p]=dist[c]+1; dq.append(p)
        else:
            cnt[p]-=1
            if cnt[p]==0: val[p]='L'; dist[p]=dist[c]+1; dq.append(p)
nonterm=[q for q in seen if not (q and last_mover_won(q))]
print("window",W,"reachable nonterminal",len(nonterm),"undecided(draw)",sum(1 for q in nonterm if q not in val))
print("start:",val.get(start),"plies",dist.get(start))
for c in range(9): print(" X opens",c, "->", {'W':'X loses','L':'X wins',None:'draw'}[val.get((c,))], dist.get((c,)))
