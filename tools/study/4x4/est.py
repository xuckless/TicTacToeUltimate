import random, math
def lines(mode):
    L=[];N=4;k=3 if mode==3 else 4
    for r in range(4):
        for c in range(4):
            for dr,dc in((0,1),(1,0),(1,1),(1,-1)):
                cells=[(r+i*dr,c+i*dc) for i in range(k)]
                if all(0<=a<4 and 0<=b<4 for a,b in cells): L.append(sum(1<<(a*4+b) for a,b in cells))
    if mode==5:
        for r in range(3):
            for c in range(3): L.append((1<<(r*4+c))|(1<<(r*4+c+1))|(1<<(r*4+c+4))|(1<<(r*4+c+5)))
    return L
for mode in (3,4,5):
    Ls=lines(mode)
    for W in (9,10,11):
        ok=0;T=200000
        for _ in range(T):
            cs=random.sample(range(16),W); a=sum(1<<c for c in cs[::2]); b=sum(1<<c for c in cs[1::2])
            if not any((a&l)==l or (b&l)==l for l in Ls): ok+=1
        raw=math.perm(16,W)*ok/T
        print(mode,W,"full-layer raw ~%.3g canonical ~%.3g"%(raw,raw/8))
