import sys, json, random
from multiprocessing import Pool
from core import game
A=dict(W=7,ghost=True,combo="consecutive",ramp=(25,35,50),crit=15,hp=100)
C125=dict(A,combo="unanswered",hp=125)
SETS={"A":(A,1.0),"C125":(C125,2.0)}
def job(a):
    sname,kx,ko,n,seed=a; cfg,red=SETS[sname]; rng=random.Random(seed)
    return [(sname,kx,ko,game(kx,ko,cfg,rng,red=red)) for _ in range(n)]
pairs=[("A","club","club",3000),("A","casual","casual",3000),("A","strong","strong",1200),
       ("A","novice","novice",4000),("A","greedy","greedy",4000),("A","random","random",4000),
       ("A","club","casual",3000),("A","strong","club",1200),
       ("C125","club","club",3000),("C125","casual","casual",3000),("C125","strong","strong",1200),
       ("C125","club","casual",3000),("C125","strong","club",1200),("C125","strong","casual",1200)]
if __name__=="__main__":
    tasks=[]; s=1
    for sn,a,b,n in pairs:
        for (x,o) in ([(a,b)] if a==b else [(a,b),(b,a)]):
            m=n if a==b else n//2
            for k in range(0,m,100): tasks.append((sn,x,o,min(100,m-k),s)); s+=1
    with Pool(10) as p: res=[r for chunk in p.imap_unordered(job,tasks) for r in chunk]
    out={}
    for sn,a,b,n in pairs:
        rows=[r for r in res if r[0]==sn and {r[1],r[2]}=={a,b} and (a!=b or (r[1]==a and r[2]==a))]
        N=len(rows); f=lambda pred: sum(1 for r in rows if pred(r))/N
        d=dict(N=N,KO=f(lambda r:r[3]["end"]=="ko"),time=f(lambda r:r[3]["end"]=="time"),
               OT=f(lambda r:r[3]["end"] in("ot","otdraw")),xWin=f(lambda r:r[3]["w"]==0),oWin=f(lambda r:r[3]["w"]==1),
               crit=f(lambda r:r[3]["crit"]))
        if a!=b: d[a+"Win"]=f(lambda r:r[3]["w"] is not None and r[1+r[3]["w"]]==a)
        out[f"{sn}:{a}-{b}"]=d
        print(f"{sn}:{a}-{b}",{k:(round(v,3) if isinstance(v,float) else v) for k,v in d.items()},flush=True)
    json.dump(out,open("results.json","w"),indent=1)
