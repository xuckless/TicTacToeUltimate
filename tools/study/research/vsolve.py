# Generalized windowed tic-tac-toe retrograde solver (adapted from ../../verify_solve.py)
# usage: vsolve.py --n 3 --k 3 --W 7 --timing before|after --wincheck after|before --blocked 4 --ban 4
import sys, argparse
from collections import deque
from statistics import median
ap=argparse.ArgumentParser()
ap.add_argument('--n',type=int,default=3); ap.add_argument('--k',type=int,default=3)
ap.add_argument('--W',type=int,default=7)
ap.add_argument('--timing',default='before')     # remove oldest before placing (vacated cell playable) or after placing
ap.add_argument('--wincheck',default='after')    # check line after removal, or before removal (only matters for timing=after)
ap.add_argument('--blocked',default='')          # comma list of permanently blocked cells
a=ap.parse_args()
n,k,W=a.n,a.k,a.W; N=n*n
blocked=set(int(x) for x in a.blocked.split(',') if x!='')
LINES=[]
for r in range(n):
  for c in range(n):
    for dr,dc in ((0,1),(1,0),(1,1),(1,-1)):
      cells=[(r+i*dr,c+i*dc) for i in range(k)]
      if all(0<=x<n and 0<=y<n for x,y in cells):
        L=frozenset(x*n+y for x,y in cells)
        if not (L & blocked): LINES.append(L)
def won(cells):
  s=set(cells); return any(L<=s for L in LINES)
FREE=[c for c in range(N) if c not in blocked]
def moves(q):
  """yield (cell, child, mover_wins)"""
  full = len(q)==W
  if a.timing=='before':
    base=q[1:] if full else q; occ=set(base)
    for c in FREE:
      if c in occ: continue
      ch=base+(c,); yield c,ch,won(ch[::-2])
  else:
    occ=set(q)
    for c in FREE:
      if c in occ: continue
      ext=q+(c,); ch=ext[1:] if full else ext
      w = won(ext[::-2]) if a.wincheck=='before' else won(ch[::-2])
      yield c,ch,w
start=(); seen={start:0}; order=[start]; dq=deque([start]); kids={}; val={}; dist={}
while dq:
  q=dq.popleft(); ch=[]; win=False
  for c,x,w in moves(q):
    if w: win=True; continue
    ch.append(x)
    if x not in seen: seen[x]=0; order.append(x); dq.append(x)
  kids[q]=ch
  if win: val[q]='W'; dist[q]=1
par={q:[] for q in seen}
for q,ch in kids.items():
  for x in ch: par[x].append(q)
cnt={q:len(kids[q]) for q in seen}
dq=deque()
for q in seen:
  if q in val: dq.append(q)
  elif cnt[q]==0: val[q]='L'; dist[q]=0; dq.append(q)   # no legal non-losing move (stalemate) -> treat as loss? flag
stalemates=sum(1 for q in seen if cnt[q]==0 and val.get(q)=='L')
# BFS-ish retrograde must process by distance order; use layered queue
dq=deque(sorted(dq,key=lambda q:dist[q]))
while dq:
  x=dq.popleft()
  for p in par[x]:
    if p in val: continue
    if val[x]=='L': val[p]='W'; dist[p]=dist[x]+1; dq.append(p)
    else:
      cnt[p]-=1
      if cnt[p]==0: val[p]='L'; dist[p]=dist[x]+1; dq.append(p)
S=len(seen); D=S-len(val)
dec=[dist[q] for q in val]
quick=sum(1 for q in val if val[q]=='W' and dist[q]<=3)
name={'W':'X loses','L':'X wins',None:'draw'}
print(f"n={n} k={k} W={W} timing={a.timing} wincheck={a.wincheck} blocked={sorted(blocked)}")
print(f"reachable nonterminal states={S} draws(cycle)={D} ({100*D/S:.2f}%) stalemate-nodes={stalemates}")
sv=val.get(start); print("start:",{'W':'X wins','L':'O wins',None:'draw'}[sv],"plies",dist.get(start))
print(f"median forced-win distance over decisive positions={median(dec) if dec else None}; %positions side-to-move wins in<=3 plies={100*quick/S:.2f}%")
ops={}
for c,x,w in moves(start):
  if w: ops[c]=('X wins',1); continue
  ops[c]=(name[val.get(x)],dist.get(x))
for c in sorted(ops): print("  X opens",c,"->",ops[c][0],"plies-after-opening",ops[c][1])
vals=set(v for v,_ in ops.values())
print("PIE:", "draw (some opening is drawn)" if 'draw' in vals else "second player (chooser) wins: every opening is decisive")
