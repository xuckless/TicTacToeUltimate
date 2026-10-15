import sys; sys.path.insert(0,'.')
from solve3 import *
def reach(r):
    start,succ,term=r; seen={start}; dq=deque([start])
    while dq:
        q=dq.popleft()
        if term(q): continue
        for c in succ(q):
            if c not in seen: seen.add(c); dq.append(c)
    return seen
A=reach(fifo_rules(6)); B=reach(cap_rules(3))
conv={(q[0::2],q[1::2],len(q)&1) for q in A}
print(len(A),len(B),len(conv),len(conv-B),len(B-conv))
print(list(B-conv)[:5])
