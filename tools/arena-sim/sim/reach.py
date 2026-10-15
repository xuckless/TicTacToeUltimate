"""Exhaustive reachability: can a double line (crit) ever happen? HP ignored (no KO).
Usage: reach.py W ghost(0/1) [n k] [cap]"""
import sys, os, json, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from arena import Rules, apply, legal
W=int(sys.argv[1]); g=bool(int(sys.argv[2]))
n=int(sys.argv[3]) if len(sys.argv)>3 else 3; k=int(sys.argv[4]) if len(sys.argv)>4 else 3
cap=int(sys.argv[5]) if len(sys.argv)>5 else 30_000_000
R=Rules(n=n,k=k,W=W,ghost=g)
t=time.time()
start=(0,0,(),0)
seen={start}; frontier=[start]; crit_moves=0; witness=None; trans=0; scoring=0; depth=0; first_crit_depth=None
parent={} if True else None
maxmine=0
while frontier and len(seen)<cap:
    nxt=[]
    depth+=1
    for key in frontier:
        x,o,q,m=key
        s=(x,o,q,10**6,10**6,0,0,m)
        for c in legal(R,s):
            s2,nl,d,u=apply(R,s,c)
            trans+=1
            if nl: scoring+=1
            if nl>=2:
                crit_moves+=1
                if witness is None:
                    witness=(key,c); first_crit_depth=depth
            k2=(s2[0],s2[1],s2[2],s2[7])
            if k2 not in seen:
                seen.add(k2); nxt.append(k2); parent[k2]=(key,c)
    frontier=nxt
def path(key):
    seq=[]
    while key in parent:
        key,c=parent[key]; seq.append(c)
    return seq[::-1]
out=dict(n=n,k=k,W=W,ghost=int(g),states=len(seen),complete=not frontier,transitions=trans,scoring_moves=scoring,
         crit_moves=crit_moves,crit_share_of_scoring=crit_moves/max(scoring,1),first_crit_ply=first_crit_depth,
         witness_moves=(path(witness[0])+[witness[1]]) if witness else None, secs=round(time.time()-t,1))
print(json.dumps(out))
