"""Re-aggregate sweep_raw.csv (comeback threshold = one base line)."""
import csv, json, os, sys
H=os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0,H)
from experiments import sweep_configs, agg
by={}
def conv(r):
    for k in ('winner',):
        r[k]=None if r[k]=='' else int(r[k])
    for k in ('t','winnerMaxDeficit','linesX','linesO','plies','critsX','critsO','maxStreakX','maxStreakO','leadChanges','avgOcc','nearEmpty'):
        r[k]=float(r[k])
    r['firstBlood']=None if r['firstBlood']=='' else float(r['firstBlood'])
    return r
with open(os.path.join(H,'results','sweep_raw.csv')) as f:
    for r in csv.DictReader(f):
        by.setdefault(r['tag'],[]).append(conv(r))
out=[]
for i,(rules,T,D,rk) in enumerate(sweep_configs()):
    tag=f"c{i}"
    eq=agg(by[tag+"|eq"]); eqc=agg(by[tag+"|eqc"]); gap=agg(by[tag+"|gap"],strong="club"); gap2=agg(by[tag+"|gap2"],strong="strong")
    out.append(dict(cfg=i,D=D,ramp=rk,rampVals="-".join(map(str,rules["ramp"])),hp=rules["hp"],T=T,W=rules["W"],ghost=int(rules["ghost"]),
        KO10_club=10*eq["KO"],KO10_casual=10*eqc["KO"],OT_club=eq["overtime"]+eq["otDraw"],OT_casual=eqc["overtime"]+eqc["otDraw"],
        draw_club=eq["draw"],xEdge_club=eq["xWin"]-eq["oWin"],comeback_club=eq["comeback"],comeback_casual=eqc["comeback"],
        leadChanges_club=eq["leadChanges"],linesPerMin_club=eq["linesPerMin"],firstBlood_club=eq["firstBloodMedian"],
        crits_club=eq["critsPerGame"],streak3_club=eq["streak3"],strongWin_clubVsCasual=gap["strongWin"],strongWin_strongVsClub=gap2["strongWin"],
        KO10_gap=10*gap["KO"]))
json.dump(out,open(os.path.join(H,'results','sweep.json'),'w'),indent=1)
