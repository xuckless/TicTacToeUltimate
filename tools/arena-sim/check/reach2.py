import sys, json
from core import G, moves, play_move
W=int(sys.argv[1]); ghost=bool(int(sys.argv[2]))
cfg=dict(W=W,ghost=ghost,combo="consecutive",ramp=(25,),crit=15,hp=10**9)
def key(g): return (tuple(g.q), tuple(g.b[i] for i in g.q), g.turn)
g0=G(10**9); seen={key(g0)}; fr=[g0]; scoring=crit=0; first=None; ply=0; wit=None
par={}
while fr:
    ply+=1; nx=[]
    for g in fr:
        for c in moves(g,W):
            h=g.copy(); n,_=play_move(h,c,cfg)
            if n: scoring+=1
            if n>1:
                crit+=1
                if first is None: first=ply
            k=key(h)
            if k not in seen: seen.add(k); nx.append(h)
    fr=nx
print(json.dumps(dict(W=W,ghost=ghost,states=len(seen),scoring=scoring,crit=crit,share=crit/max(1,scoring),first=first)))
