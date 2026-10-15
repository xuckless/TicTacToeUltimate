import sys
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
import os; f=TTFont(os.environ["FONT"]); gs=f.getGlyphSet(); cmap=f.getBestCmap(); upm=f['head'].unitsPerEm
hm=f['hmtx']
def text_path(text,size,x0,y0,track=0):
    s=size/upm; x=x0; out=[]
    for ch in text:
        g=cmap.get(ord(ch))
        if g is None: continue
        pen=SVGPathPen(gs)
        tp=TransformPen(pen,(s,0,0,-s,x,y0))
        gs[g].draw(tp); d=pen.getCommands()
        if d: out.append(d)
        x+=hm[g][0]*s+track
    return ' '.join(out), x-x0-track
if __name__=='__main__':
    import json
    text,size=sys.argv[2],float(sys.argv[3]); track=float(sys.argv[4]) if len(sys.argv)>4 else 0
    d,w=text_path(text,size,0,0,track)
    print(json.dumps({'d':d,'w':w}))
