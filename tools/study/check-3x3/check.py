# Independent checker: cell-age board representation + level-synchronous fixed-point solve.
import sys, statistics
WINS=[{0,1,2},{3,4,5},{6,7,8},{0,3,6},{1,4,7},{2,5,8},{0,4,8},{2,4,6}]
def line(cells): return any(w<=cells for w in WINS)

# State: (cells, turn). cells[i] = None or (owner, age). owner 0=X 1=O.
# mode 'fifo': age global plies-ago (1=newest), pieces with age>W gone.
# mode 'cap' : age per owner (1=newest of that owner), pieces with age>K gone.
def make(mode, N, vac_ok=True, vanish_nowin=False, ban_center_first=False):
    start=(tuple([None]*9),0)
    def moves(s):
        cells,t=s
        if mode=='fifo':
            vac=[i for i,c in enumerate(cells) if c and c[1]==N]
        else:
            vac=[i for i,c in enumerate(cells) if c and c[0]==t and c[1]==N]
        out=[]
        for i in range(9):
            if cells[i] is not None and i not in vac: continue
            if i in vac and not vac_ok: continue
            if ban_center_first and s==start and i==4: continue
            nc=[]
            for j,c in enumerate(cells):
                if c is None or j in vac: nc.append(None); continue
                if mode=='fifo': nc.append((c[0],c[1]+1))
                else: nc.append((c[0],c[1]+1) if c[0]==t else c)
            nc[i]=(t,1)
            out.append((i,(tuple(nc),1-t)))
        return out
    def lost(s):  # previous mover (1-t) has a line
        cells,t=s; p=1-t
        mine={i for i,c in enumerate(cells) if c and c[0]==p}
        if vanish_nowin and mode=='fifo':
            mine={i for i in mine if cells[i][1]!=N}  # piece leaving on next placement
        return line(mine)
    return start,moves,lost

def solve(rules):
    start,moves,lost=rules
    seen={start}; stack=[start]; kids={}
    while stack:
        s=stack.pop()
        if lost(s): kids[s]=None; continue
        ch=moves(s); kids[s]=ch
        for _,c in ch:
            if c not in seen: seen.add(c); stack.append(c)
    val={s:('L',0) for s in seen if kids[s] is None}
    nonterm=[s for s in seen if kids[s] is not None]
    k=0; pending=set(nonterm)
    while True:
        k+=1; new={}
        for s in pending:
            vs=[val.get(c) for _,c in kids[s]]
            if not vs: continue
            if any(v and v[0]=='L' for v in vs): new[s]=('W',k)
            elif all(v and v[0]=='W' for v in vs): new[s]=('L',k)
        if not new: break
        val.update(new); pending-=new.keys()
    def side(v,mover):
        if v is None: return 'draw'
        return 'X win' if (v[0]=='W')==(mover==0) else 'O win'
    v0=val.get(start)
    dec=[val[s][1] for s in nonterm if s in val]
    stuck=sum(1 for s in nonterm if not kids[s])
    r=dict(reach=len(seen),nonterm=len(nonterm),stuck=stuck,start=side(v0,0),plies=v0[1] if v0 else None,
           draw=round(100*sum(1 for s in nonterm if s not in val)/len(nonterm),2),
           med=statistics.median(dec) if dec else None,
           w3=round(100*sum(1 for s in nonterm if s in val and val[s][0]=='W' and val[s][1]<=3)/len(nonterm),1),
           open={i:(side(val.get(c),1),val[c][1] if c in val else None) for i,c in kids[start]})
    return r

V={
 'a W5 vp':('fifo',5,dict()),
 'a W6 vp':('fifo',6,dict()),
 'W6 vnp':('fifo',6,dict(vac_ok=False)),
 'a W7 vp':('fifo',7,dict()),
 'W7 vnp':('fifo',7,dict(vac_ok=False)),
 'b K3 classic vnp':('cap',3,dict(vac_ok=False)),
 'b K3 vp':('cap',3,dict()),
 'c W7 center banned':('fifo',7,dict(ban_center_first=True)),
 'd W7 vanish no win':('fifo',7,dict(vanish_nowin=True)),
 'a W9 vp':('fifo',9,dict()),
}
for name in sys.argv[1:] or V:
    m,n,kw=V[name]; print(name, solve(make(m,n,**kw)), flush=True)
