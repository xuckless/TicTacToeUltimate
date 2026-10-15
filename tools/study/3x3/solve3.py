import sys, json, statistics
from collections import deque
LINES=[(0,1,2),(3,4,5),(6,7,8),(0,3,6),(1,4,7),(2,5,8),(0,4,8),(2,4,6)]
LM=[sum(1<<c for c in L) for L in LINES]
def hasline(cells):
    m=0
    for c in cells: m|=1<<c
    return any(m&l==l for l in LM)

# ---- rule sets. state always hashable; t = side to move (0=X,1=O)
def fifo_rules(W, vacated_ok=True, vanish_nowin=False):
    # state (q, t): q = global queue oldest-first, t = side to move (explicit so colour-swapped twins are not merged)
    def term(s):
        q,t=s
        if not q: return False
        mine=list(q[len(q)-1::-2])
        if vanish_nowin and len(q)==W and (W%2==1):  # q[0] belongs to last mover when W odd
            mine=[c for c in mine if c!=q[0]]
        return hasline(mine)
    def succ(s):
        q,t=s
        if len(q)==W: base=q[1:]; vac=q[0]
        else: base=q; vac=None
        occ=set(base)
        return [(base+(c,),1-t) for c in range(9) if c not in occ and (vacated_ok or c!=vac)]
    return ((),0), succ, term
def cap_rules(K, vacated_ok=True):
    # state (xs, os, t); xs/os oldest-first
    def term(s):
        xs,os,t=s
        if not xs: return False
        return hasline(os if t==0 else xs)
    def succ(s):
        xs,os,t=s
        mine=xs if t==0 else os
        if len(mine)==K: base=mine[1:]; vac=mine[0]
        else: base=mine; vac=None
        occ=set(base)|set(os if t==0 else xs)
        out=[]
        for c in range(9):
            if c in occ or (not vacated_ok and c==vac): continue
            nm=base+(c,)
            out.append((nm,os,1) if t==0 else (xs,nm,0))
        return out
    return ((),(),0), succ, term

def solve(rules, start_children_filter=None):
    start,succ,term=rules
    seen={start:0}; order=[start]; dq=deque([start]); children={}
    while dq:
        q=dq.popleft()
        if term(q): children[q]=[]; continue
        ch=succ(q)
        if q==start and start_children_filter: ch=[c for c in ch if start_children_filter(c)]
        children[q]=ch
        for c in ch:
            if c not in seen: seen[c]=0; dq.append(c)
    parents={q:[] for q in seen}
    for q,ch in children.items():
        for c in ch: parents[c].append(q)
    val={}; dist={}; cnt={q:len(children[q]) for q in seen}
    dq=deque()
    for q in seen:
        if term(q): val[q]='L'; dist[q]=0; dq.append(q)
        # non-terminal with no legal move (only W=9 + vacated-not-playable): left undecided = draw
    while dq:
        c=dq.popleft()
        for p in parents[c]:
            if p in val: continue
            if val[c]=='L': val[p]='W'; dist[p]=dist[c]+1; dq.append(p)
            else:
                cnt[p]-=1
                if cnt[p]==0: val[p]='L'; dist[p]=dist[c]+1; dq.append(p)
    nonterm=[q for q in seen if not term(q)]
    nstuck=sum(1 for q in nonterm if not children[q])
    draws=sum(1 for q in nonterm if q not in val)
    dec=[dist[q] for q in nonterm if q in val]
    w3=sum(1 for q in nonterm if val.get(q)=='W' and dist[q]<=3)
    def X(v,mover):  # mover 0=X to move
        if v is None: return 'draw'
        win = (v=='W')
        return ('X win' if (win==(mover==0)) else 'O win')
    res=dict(reachable=len(seen), nonterminal=len(nonterm), stuck=nstuck,
             start=X(val.get(start),0), start_plies=dist.get(start),
             draw_pct=round(100*draws/len(nonterm),3),
             median_forced_dist=statistics.median(dec) if dec else None,
             win_le3_pct=round(100*w3/len(nonterm),3))
    ops={}
    for c in children[start]:
        cell = c[0][-1]
        ops[cell]=(X(val.get(c),1), dist.get(c))
    res['openings']=ops
    # pie: O sees position after opening; O (to move) keeps if 'W', swaps if 'L' -> chooser wins unless draw
    draw_open=[k for k,v in ops.items() if v[0]=='draw']
    res['pie']= ('draw (X opens %s)'%draw_open) if draw_open else 'second player (chooser) wins'
    return res

if __name__=='__main__':
    out={}
    for W in [5,6,7,8,9]:
        out[f'fifo W={W} vacated-playable']=solve(fifo_rules(W))
        out[f'fifo W={W} vacated-NOT-playable']=solve(fifo_rules(W,vacated_ok=False))
    for K in [3,4]:
        out[f'cap K={K} vacated-playable']=solve(cap_rules(K))
        out[f'cap K={K} vacated-NOT-playable']=solve(cap_rules(K,vacated_ok=False))
    out['fifo W=7 center banned move1']=solve(fifo_rules(7), start_children_filter=lambda c:c[0][-1]!=4)
    out['fifo W=7 vanishing piece cannot win']=solve(fifo_rules(7,vanish_nowin=True))
    for k,v in out.items(): print(k, json.dumps(v))
