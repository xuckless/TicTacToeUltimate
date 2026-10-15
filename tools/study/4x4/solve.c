// Retrograde solver for "disappearing" tic-tac-toe variants on NxN.
// State = ordered queue of occupied cells (oldest first), most recent = last mover.
// Ownership alternates backwards from the last element. Global FIFO of length W:
// when queue is full, q[0] is removed before placing. Per-player cap K == W=2K
// (q[0] is then the mover's own oldest piece).
// Usage: solve N W mode [noreplay] [capLog2]
//   mode: 3 = 3-in-a-row, 4 = 4-in-a-row, 5 = 4-in-a-row OR 2x2 square
//   noreplay: 1 = may not place on the cell just vacated (default 0 = allowed)
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <time.h>

static int N, NC, W, MODE, NOREPLAY;
static uint32_t lines[128]; static int nlines;
static int sym[8][16];
static uint64_t CAP, MASK;
static uint64_t *keys; static uint16_t *val; static uint8_t *cnt;
static uint32_t *order; static uint64_t nstates = 0;
#define EMPTY UINT64_MAX

static void addline(int r0,int c0,int dr,int dc,int len){
  for(int r=r0,c=c0,k=0;k<len;k++,r+=dr,c+=dc) if(r<0||r>=N||c<0||c>=N) return;
  uint32_t m=0; for(int r=r0,c=c0,k=0;k<len;k++,r+=dr,c+=dc) m|=1u<<(r*N+c);
  lines[nlines++]=m;
}
static void setup(void){
  int L = (MODE==3)?3:(MODE==4||MODE==5)?4:MODE;
  if (N==3) L=3;
  for(int r=0;r<N;r++)for(int c=0;c<N;c++){
    addline(r,c,0,1,L); addline(r,c,1,0,L); addline(r,c,1,1,L); addline(r,c,1,-1,L);
  }
  if(MODE==5) for(int r=0;r+1<N;r++)for(int c=0;c+1<N;c++)
    lines[nlines++]=(1u<<(r*N+c))|(1u<<(r*N+c+1))|(1u<<((r+1)*N+c))|(1u<<((r+1)*N+c+1));
  for(int s=0;s<8;s++)for(int r=0;r<N;r++)for(int c=0;c<N;c++){
    int rr=r,cc=c,t;
    if(s&1){ cc=N-1-cc; }
    if(s&2){ rr=N-1-rr; }
    if(s&4){ t=rr; rr=cc; cc=t; }
    sym[s][r*N+c]=rr*N+cc;
  }
}
static inline int haswin(uint32_t m){ for(int i=0;i<nlines;i++) if((m&lines[i])==lines[i]) return 1; return 0; }

// key: len in low 4 bits, cell i in bits 4+4i
static inline int qlen(uint64_t k){ return k&15; }
static inline int qget(uint64_t k,int i){ return (k>>(4+4*i))&15; }
static inline uint64_t mk(const int*q,int L){ uint64_t k=L; for(int i=0;i<L;i++) k|=(uint64_t)q[i]<<(4+4*i); return k; }
static inline uint64_t canon(const int*q,int L){
  uint64_t best=EMPTY;
  for(int s=0;s<8;s++){ uint64_t k=L; const int*S=sym[s]; for(int i=0;i<L;i++) k|=(uint64_t)S[q[i]]<<(4+4*i); if(k<best)best=k; }
  return best;
}
static int orbit(uint64_t key){ // number of distinct images
  int L=qlen(key),q[16]; for(int i=0;i<L;i++)q[i]=qget(key,i);
  uint64_t im[8]; int n=0;
  for(int s=0;s<8;s++){ uint64_t k=L; for(int i=0;i<L;i++) k|=(uint64_t)sym[s][q[i]]<<(4+4*i);
    int d=1; for(int j=0;j<n;j++) if(im[j]==k){d=0;break;} if(d) im[n++]=k; }
  return n;
}
static inline uint64_t hsh(uint64_t k){ k^=k>>33; k*=0xff51afd7ed558ccdULL; k^=k>>33; k*=0xc4ceb9fe1a85ec53ULL; k^=k>>33; return k; }
static inline int64_t find(uint64_t k){
  uint64_t h=hsh(k)&MASK; while(keys[h]!=EMPTY){ if(keys[h]==k) return h; h=(h+1)&MASK; } return -1;
}
static inline int64_t insert(uint64_t k,int*isnew){
  uint64_t h=hsh(k)&MASK; while(keys[h]!=EMPTY){ if(keys[h]==k){*isnew=0;return h;} h=(h+1)&MASK; }
  keys[h]=k; *isnew=1; order[nstates++]=(uint32_t)h;
  if(nstates > CAP*0.85){ fprintf(stderr,"hash too full\n"); exit(2);} return h;
}
static inline uint32_t ownmask(const int*q,int L,int fromEnd){ // pieces at L-1-fromEnd, L-3-fromEnd...
  uint32_t m=0; for(int i=L-1-fromEnd;i>=0;i-=2) m|=1u<<q[i]; return m;
}
// generate children (canonical keys) of state; returns n; sets *immwin if a move wins immediately
static int children(uint64_t key,uint64_t*out,int*immwin){
  int L=qlen(key),q[17]; for(int i=0;i<L;i++)q[i]=qget(key,i);
  int base[17],BL; int removed=-1;
  if(L==W){ removed=q[0]; for(int i=1;i<L;i++)base[i-1]=q[i]; BL=L-1; } else { for(int i=0;i<L;i++)base[i]=q[i]; BL=L; }
  uint32_t occ=0; for(int i=0;i<BL;i++) occ|=1u<<base[i];
  uint32_t mine=ownmask(base,BL,1); // mover's pieces in base: base[BL-2], ...
  int n=0; *immwin=0;
  for(int e=0;e<NC;e++){ if(occ>>e&1) continue; if(NOREPLAY && e==removed) continue;
    if(haswin(mine|1u<<e)){ *immwin=1; continue; }
    base[BL]=e; uint64_t c=canon(base,BL+1);
    int d=1; for(int j=0;j<n;j++) if(out[j]==c){d=0;break;} if(d) out[n++]=c;
  }
  return n;
}
// distinct canonical reachable nonterminal predecessors
static int preds(uint64_t key,int64_t*out){
  int L=qlen(key),q[17]; for(int i=0;i<L;i++)q[i]=qget(key,i);
  if(L==0) return 0;
  int n=0; int p[17]; uint64_t pk[20];
  // p1: drop last (no removal happened)
  { for(int i=0;i<L-1;i++)p[i]=q[i];
    uint32_t lm = (L-1>0)? ownmask(p,L-1,0):0;
    if(!(L-1>0 && haswin(lm))) pk[n++]=canon(p,L-1); }
  if(L==W){
    uint32_t occ=0; for(int i=0;i<L-1;i++) occ|=1u<<q[i];
    for(int r=0;r<NC;r++){ if(occ>>r&1) continue; if(NOREPLAY && r==q[L-1]) continue;
      p[0]=r; for(int i=0;i<L-1;i++)p[i+1]=q[i];
      if(haswin(ownmask(p,L,0))) continue;
      uint64_t c=canon(p,L); int d=1; for(int j=0;j<n;j++) if(pk[j]==c){d=0;break;} if(d) pk[n++]=c; }
  }
  int m=0; for(int j=0;j<n;j++){ int64_t h=find(pk[j]); if(h>=0) out[m++]=h; }
  return m;
}
static int cmpu(const void*a,const void*b){ int x=*(const int*)a,y=*(const int*)b; return x-y; }

int main(int argc,char**argv){
  N=atoi(argv[1]); W=atoi(argv[2]); MODE=atoi(argv[3]); NOREPLAY=argc>4?atoi(argv[4]):0;
  int lg=argc>5?atoi(argv[5]):24; NC=N*N; setup();
  CAP=1ULL<<lg; MASK=CAP-1;
  keys=malloc(CAP*8); val=calloc(CAP,2); cnt=calloc(CAP,1); order=malloc(CAP*4);
  if(!keys||!val||!cnt||!order){fprintf(stderr,"oom\n");return 1;}
  memset(keys,0xff,CAP*8);
  clock_t t0=clock();
  int isnew; int64_t sh=insert(0,&isnew);
  uint64_t ch[32]; int iw;
  // BFS enumeration
  for(uint64_t i=0;i<nstates;i++){
    uint32_t h=order[i]; int n=children(keys[h],ch,&iw);
    if(iw) val[h]=1; cnt[h]=n;
    for(int j=0;j<n;j++) insert(ch[j],&isnew);
  }
  fprintf(stderr,"enumerated %llu canonical states in %.1fs\n",(unsigned long long)nstates,(double)(clock()-t0)/CLOCKS_PER_SEC);
  // retrograde
  uint32_t *Q=malloc(nstates*4); uint64_t qh=0,qt=0;
  for(uint64_t i=0;i<nstates;i++){ uint32_t h=order[i]; if(val[h]==1) Q[qt++]=h; }
  int64_t pr[40]; int maxd=0;
  while(qh<qt){
    uint32_t c=Q[qh++]; uint16_t v=val[c]; int win=v<32768; int d=win?v:v-32768; if(d>maxd)maxd=d;
    int n=preds(keys[c],pr);
    for(int j=0;j<n;j++){ int64_t p=pr[j]; if(val[p]) continue;
      if(!win){ val[p]=d+1; Q[qt++]=p; }
      else { if(--cnt[p]==0){ val[p]=32768+d+1; Q[qt++]=p; } }
    }
  }
  fprintf(stderr,"retrograde done in %.1fs, maxdist %d\n",(double)(clock()-t0)/CLOCKS_PER_SEC,maxd);
  // stats (orbit-weighted = raw counts, and canonical counts)
  double raw=0,rawdraw=0,rawdec=0,rawwin3=0; uint64_t cdraw=0;
  static double hist[70000]; memset(hist,0,sizeof hist);
  for(uint64_t i=0;i<nstates;i++){ uint32_t h=order[i]; double w=orbit(keys[h]); raw+=w; uint16_t v=val[h];
    if(!v){ rawdraw+=w; cdraw++; continue; }
    int win=v<32768; int d=win?v:v-32768; rawdec+=w; hist[d]+=w; if(win&&d<=3) rawwin3+=w; }
  double acc=0; int med=-1; for(int d=0;d<70000;d++){ acc+=hist[d]; if(acc>=rawdec/2){med=d;break;} }
  printf("N=%d W=%d mode=%d noreplay=%d lines=%d\n",N,W,MODE,NOREPLAY,nlines);
  printf("reachable nonterminal: canonical %llu, raw %.0f\n",(unsigned long long)nstates,raw);
  printf("draws: canonical %llu, raw %.0f (%.4f%% of raw nonterminal)\n",(unsigned long long)cdraw,rawdraw,100*rawdraw/raw);
  printf("median forced distance over decisive (raw-weighted): %d ; %% positions side-to-move wins in <=3 plies: %.3f%%\n",med,100*rawwin3/raw);
  printf("max distance: %d\n",maxd);
  uint16_t sv=val[sh];
  if(!sv) printf("START: draw\n"); else if(sv<32768) printf("START: X wins in %d plies\n",sv); else printf("START: O wins in %d plies\n",sv-32768);
  int drawopen=0, xw=0, xl=0;
  for(int c=0;c<NC;c++){ int q[1]={c}; int64_t h=find(canon(q,1)); uint16_t v=val[h];
    const char*s; int d=0;
    if(!v){s="draw";drawopen++;} else if(v<32768){s="X loses";d=v;xl++;} else {s="X wins";d=v-32768;xw++;}
    printf(" open %2d (r%d c%d): %s %d\n",c,c/N,c%N,s,d);
  }
  printf("PIE: openings X-win %d, X-loss %d, draw %d -> %s\n",xw,xl,drawopen,
    drawopen? "pie game is a DRAW (X picks a drawn opening)":"O (chooser) wins under pie");
  return 0;
}
