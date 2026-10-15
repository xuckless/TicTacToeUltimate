import random
from multiprocessing import Pool
from core import game
A=dict(W=7,ghost=True,combo="consecutive",ramp=(25,35,50),crit=15,hp=100)
C=dict(A,combo="unanswered",hp=125)
def job(a):
    cfg,red,seed=a; rng=random.Random(seed); r=[game("strong","strong",cfg,rng,red=red) for _ in range(100)]
    return [x["w"] for x in r],[x["w"] for x in r if x["end"]=="ot"]
if __name__=="__main__":
    for name,cfg,red in (("A",A,1.0),("C125",C,2.0)):
        with Pool(10) as p: res=p.map(job,[(cfg,red,777000+i+(0 if name=="A" else 5000)) for i in range(60)])
        w=[x for a,_ in res for x in a]; ot=[x for _,b in res for x in b]
        N=len(w); print(name,"N",N,"xEdge %.3f"%((w.count(0)-w.count(1))/N),"OT X share %.3f (n=%d)"%(ot.count(0)/len(ot),len(ot)))
