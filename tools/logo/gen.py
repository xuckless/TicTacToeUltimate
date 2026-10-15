import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from text2path import text_path
OUT=sys.argv[1]
P='#7442ef'; SH='#4a22b0'; X='#38d6ff'; O='#ff9f3a'; INK='#f5f0ff'; MUTED='#b9aadb'; BG='#120a22'; INKL='#1b1030'; PL='#6b3df0'
def X_shape(cx,cy,half,sw,fill,op=1):
    L=2*half*1.41421+sw
    r=f'<rect x="{cx-L/2:.1f}" y="{cy-sw/2:.1f}" width="{L:.1f}" height="{sw}" rx="{sw/2}"'
    return (f'<g fill="{fill}" fill-opacity="{op}">{r} transform="rotate(45 {cx} {cy})"/>'
            f'{r} transform="rotate(-45 {cx} {cy})"/></g>')
def O_shape(cx,cy,r,sw,fill,op=1):
    ro=r+sw/2; ri=r-sw/2
    d=(f'M{cx-ro} {cy}a{ro} {ro} 0 1 0 {2*ro} 0a{ro} {ro} 0 1 0 {-2*ro} 0z'
       f'M{cx-ri} {cy}a{ri} {ri} 0 1 0 {2*ri} 0a{ri} {ri} 0 1 0 {-2*ri} 0z')
    return f'<path fill="{fill}" fill-opacity="{op}" fill-rule="evenodd" d="{d}"/>'
def mark_body(ghost=True, sw=104):
    g=[f'<rect x="0" y="44" width="1024" height="980" rx="232" fill="{SH}"/>',
       f'<rect x="0" y="0" width="1024" height="980" rx="232" fill="{P}"/>']
    g.append(X_shape(318,318,100,sw,X))
    g.append(O_shape(706,662,104,sw,O))
    if ghost:
        g.append(O_shape(706,318,104,sw,'#ffffff',.26))
        g.append(X_shape(318,662,100,sw,'#ffffff',.26))
    return '\n'.join(g)
def svg(w,h,body,title):
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" aria-label="{title}"><title>{title}</title>\n{body}\n</svg>\n'
open(f'{OUT}/ttt-app-icon.svg','w').write(svg(1024,1024,mark_body(),'Tic Tac Toe Ultimate'))
# favicon: no ghosts, chunkier
fav=(f'<rect x="0" y="3" width="64" height="61" rx="15" fill="{SH}"/><rect x="0" y="0" width="64" height="61" rx="15" fill="{P}"/>'
     + X_shape(20,20,8.5,9,X) + O_shape(44,41,9.5,9,O))
open(f'{OUT}/favicon.svg','w').write(svg(64,64,fav,'TTT Ultimate'))

def wordmark(dark=True, stacked=False):
    ink = INK if dark else INKL; sub = MUTED if dark else '#5a4a80'
    top,tw = text_path('TIC TAC TOE',58,0,0,10)
    big,bw = text_path('ULTIMATE',150,0,0,4)
    icon = 200
    if not stacked:
        x0 = icon+44; W = int(x0+max(tw,bw)+8); H = 212
        body = f'<g transform="scale({icon/1024})">{mark_body()}</g>'
        body += f'<path transform="translate({x0},60)" fill="{sub}" d="{top}"/>'
        body += f'<path transform="translate({x0-4},196)" fill="{ink}" d="{big}"/>'
    else:
        icon=280; W=int(max(bw,icon)+40); H=icon+60+60+150+20
        body = f'<g transform="translate({(W-icon)/2},0) scale({icon/1024})">{mark_body()}</g>'
        body += f'<path transform="translate({(W-tw)/2},{icon+100})" fill="{sub}" d="{top}"/>'
        body += f'<path transform="translate({(W-bw)/2},{icon+250})" fill="{ink}" d="{big}"/>'
    return svg(W,H,body,'Tic Tac Toe Ultimate')
open(f'{OUT}/wordmark-dark.svg','w').write(wordmark(True))
open(f'{OUT}/wordmark-light.svg','w').write(wordmark(False))
open(f'{OUT}/wordmark-stacked-dark.svg','w').write(wordmark(True,True))
print('ok')
