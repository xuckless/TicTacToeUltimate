import sys, os, time, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from arena import *
R = Rules()
rng = random.Random(1)
for a,b,N in [("random","random",500),("greedy","greedy",500),("mm2","mm2",50),("mm3","mm3",10),("club","casual",50)]:
    t=time.time(); res=[play(R,get_bot(a),get_bot(b),rng) for _ in range(N)]
    dt=(time.time()-t)/N
    import collections
    print(a,b,f"{dt*1000:.1f}ms/game", collections.Counter(r['end'] for r in res), sum(r['linesX']+r['linesO'] for r in res)/N, sum(r['plies'] for r in res)/N)
