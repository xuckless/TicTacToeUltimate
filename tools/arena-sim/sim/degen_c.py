import sys, os, json
H=os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0,H)
from experiments import BASE, CLOCK, both_seats, run_tasks, write_csv, agg
if __name__=="__main__":
    rules=dict(BASE, combo="unanswered"); extra={"red_mult":2.0}
    pairs=[("turtle","turtle"),("turtle","casual"),("turtle","club"),("turtle","strong"),("clubstall","club"),("clubstall","casual")]
    tasks=[]; s=90000
    for a,b in pairs:
        tasks+=both_seats(f"{a}-{b}",rules,a,b,2000,s,CLOCK,extra); s+=2
    rows=run_tasks(tasks); write_csv("degen_rulesetC_raw",rows)
    out={f"{a}-{b}":agg([r for r in rows if r["tag"]==f"{a}-{b}"],strong=a if a!=b else None) for a,b in pairs}
    json.dump(out,open(os.path.join(H,"results","degen_rulesetC.json"),"w"),indent=1)
    for k,g in out.items(): print(k,'firstSeatBotWin=%s'%g.get('strongWin'),'KO=%.2f OT=%.2f otDraw=%.2f lpm=%.2f noBlood=%.3f'%(g['KO'],g['overtime'],g['otDraw'],g['linesPerMin'],g['noBlood']))
