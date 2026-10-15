import json, csv, os, sys
H=os.path.dirname(os.path.abspath(__file__)); d=json.load(open(os.path.join(H,'results','sweep.json')))
with open(os.path.join(H,'results','sweep_summary.csv'),'w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=list(d[0].keys())); w.writeheader(); w.writerows(d)
def score(r):
    # target: KO 2-4 per 10 (club & casual avg), strong win 0.70-0.80 (club vs casual & strong vs club avg), low OT, comebacks
    ko=(r['KO10_club']+r['KO10_casual'])/2
    sw=(r['strongWin_clubVsCasual']+r['strongWin_strongVsClub'])/2
    ot=(r['OT_club']+r['OT_casual'])/2
    pen=max(0,2-ko)+max(0,ko-4)+10*max(0,0.70-sw)+10*max(0,sw-0.80)+3*ot+2*max(0,0.25-r['comeback_club'])+5*max(0,abs(r['xEdge_club'])-0.06)
    return pen,ko,sw,ot
rows=sorted(d,key=lambda r:score(r)[0])
cols=['D','rampVals','hp','T','W','ghost']
print('rank',*cols,'pen','KO10eq','strongAvg','cVsCas','sVsClub','OTeq','comeback','xEdge','lpm','fb','crits',sep='\t')
for i,r in enumerate(rows[:int(sys.argv[1]) if len(sys.argv)>1 else 25]):
    p,ko,sw,ot=score(r)
    print(i+1,*[r[c] for c in cols],'%.2f'%p,'%.1f'%ko,'%.2f'%sw,'%.2f'%r['strongWin_clubVsCasual'],'%.2f'%r['strongWin_strongVsClub'],'%.2f'%ot,'%.2f'%r['comeback_club'],'%+.2f'%r['xEdge_club'],'%.1f'%r['linesPerMin_club'],r['firstBlood_club'],'%.2f'%r['crits_club'],sep='\t')
# marginal effects
import statistics as st
print()
for key in ['D','ramp','hp','T','W','ghost']:
    vals=sorted(set(r[key] for r in d), key=str)
    for v in vals:
        rs=[r for r in d if r[key]==v and not (r['W']==5 and r['ghost']==1)]
        print(key,v,'KO10eq %.2f'%st.mean((r['KO10_club']+r['KO10_casual'])/2 for r in rs),
              'OT %.2f'%st.mean((r['OT_club']+r['OT_casual'])/2 for r in rs),
              'cVsCas %.2f'%st.mean(r['strongWin_clubVsCasual'] for r in rs),'sVsClub %.2f'%st.mean(r['strongWin_strongVsClub'] for r in rs),
              'comeback %.2f'%st.mean(r['comeback_club'] for r in rs), 'xEdge %+.3f'%st.mean(r['xEdge_club'] for r in rs), sep='  ')
