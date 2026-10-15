// Perfect-index, multithreaded retrograde solver (no symmetry, raw states) for 4x4.
// State = ordered queue (oldest first) of distinct cells, length L<=W; ranked as an
// injective sequence. Layer W stored as nibbles (needs 16-(W-1) <= 9 => W>=8), others bytes.
// Usage: psolve W mode [threads] [noreplay] [rule]   mode: 3=3-row, 4=4-row, 5=4-row or 2x2
//   rule: w1 (default, legacy: every piece counts toward a win)
//         w0 (ARCHITECTURE.md 4.2 step 7: exclude NextToVanish(stateAfterPlacement) when it is
//             the mover's; under GlobalFifo that only happens for odd W, so w0 == w1 for even W)
//         g  ("ghost": exclude the mover's piece that vanishes within the next two plies, i.e. the
//             piece that is gone before the mover can move again; identical to w0 for odd W)
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

#define NC 16
static int W, MODE, NT, NOREPLAY, RULE; // RULE 0=w1 1=w0 2=g
static uint32_t lines[64]; static int nlines;
static uint64_t P[17], SZ[17]; // P[L] = 16!/(16-L)!
static uint8_t *arr[17];
// unified codes: 0 invalid, 1..16 cnt, 32 WCUR,33 WNEXT,34 WDONE,35 LCUR,36 LNEXT,37 LDONE
enum { WCUR=32, WNEXT, WDONE, LCUR, LNEXT, LDONE };
static inline int enc_n(int c){ return c<32? c : c-32+10; }
static inline int dec_n(int n){ return n<10? n : n-10+32; }

static inline int getc_(int L, uint64_t i){
  if(L==W){ uint8_t b=__atomic_load_n(&arr[L][i>>1],__ATOMIC_RELAXED); return dec_n((i&1)? b>>4 : b&15); }
  return __atomic_load_n(&arr[L][i],__ATOMIC_RELAXED);
}
// CAS update of code: returns 1 on success
static inline int casc(int L,uint64_t i,int oldc,int newc){
  if(L==W){
    uint8_t *p=&arr[L][i>>1]; int sh=(i&1)?4:0;
    uint8_t b=__atomic_load_n(p,__ATOMIC_RELAXED);
    for(;;){ if(dec_n((b>>sh)&15)!=oldc) return 0;
      uint8_t nb=(b & ~(15<<sh)) | (enc_n(newc)<<sh);
      if(__atomic_compare_exchange_n(p,&b,nb,1,__ATOMIC_RELAXED,__ATOMIC_RELAXED)) return 1; }
  } else { uint8_t o=oldc; return __atomic_compare_exchange_n(&arr[L][i],&o,(uint8_t)newc,0,__ATOMIC_RELAXED,__ATOMIC_RELAXED); }
}
static inline void setc(int L,uint64_t i,int c){ // non-concurrent init (each thread owns whole bytes)
  if(L==W){ uint8_t *p=&arr[L][i>>1]; if(i&1) *p=(*p&15)|(enc_n(c)<<4); else *p=(*p&0xF0)|enc_n(c); }
  else arr[L][i]=c;
}
static uint64_t P15[17]; static int sym[8][16];
static const int REP[3]={0,1,5};
static inline uint64_t rankq(const int*q,int L){ // q must be canonical (q0 in {0,1,5})
  if(L==0) return 0;
  int cls = q[0]==0?0: q[0]==1?1:2;
  uint64_t r=0; uint32_t used=1u<<q[0];
  for(int i=1;i<L;i++){ int c=q[i]; int less=__builtin_popcount(~used & ((1u<<c)-1)); r=r*(NC-i)+less; used|=1u<<c; }
  return cls*P15[L-1]+r;
}
static inline void unrank(uint64_t r,int L,int*q){
  if(L==0) return;
  int cls=r/P15[L-1]; r%=P15[L-1]; q[0]=REP[cls];
  int d[17]; for(int i=L-1;i>=1;i--){ d[i]=r%(NC-i); r/=(NC-i); }
  uint32_t used=1u<<q[0];
  for(int i=1;i<L;i++){ int k=d[i]; for(int c=0;c<NC;c++){ if(used>>c&1) continue; if(k==0){q[i]=c;used|=1u<<c;break;} k--; } }
}
static inline void canon(const int*q,int L,int*out){ // lexicographic min image
  for(int i=0;i<L;i++) out[i]=q[i];
  for(int s=1;s<8;s++){ const int*S=sym[s]; int i=0;
    for(;i<L;i++){ int a=S[q[i]]; if(a!=out[i]) break; }
    if(i<L && S[q[i]]<out[i]) for(int j=0;j<L;j++) out[j]=S[q[j]]; }
}
static inline int iscanon(const int*q,int L){
  for(int s=1;s<8;s++){ const int*S=sym[s]; for(int i=0;i<L;i++){ int a=S[q[i]]; if(a<q[i]) return 0; if(a>q[i]) break; } }
  return 1;
}
static inline int orbitsz(const int*q,int L){ int st=0; for(int s=0;s<8;s++){ int i=0; for(;i<L;i++) if(sym[s][q[i]]!=q[i]) break; if(i==L) st++; } return 8/st; }
static inline uint64_t pk(const int*q,int L){ uint64_t k=L; for(int i=0;i<L;i++) k|=(uint64_t)q[i]<<(4+4*i); return k; }
static inline int haswin(uint32_t m){ for(int i=0;i<nlines;i++) if((m&lines[i])==lines[i]) return 1; return 0; }
static inline uint32_t ownmask(const int*q,int L,int fromEnd){ uint32_t m=0; for(int i=L-1-fromEnd;i>=0;i-=2) m|=1u<<q[i]; return m; }
// Queue q (length L) right after a placement; the last mover owns q[L-1], q[L-3], ...
// Returns the bit of the last mover's piece that may not count toward its win, or 0.
static inline uint32_t ghostbit(const int*q,int L){
  if(RULE==0) return 0;
  if(L==W) return (W&1)? 1u<<q[0] : (RULE==2? 1u<<q[1] : 0);        // q[0] mover's iff W odd
  if(RULE==2 && L==W-1 && !(W&1)) return 1u<<q[0];                  // filled by opp, then mover drops q[0]
  return 0;
}
// mover's pieces that count toward a win right after its placement
static inline uint32_t livemask(const int*q,int L){ return ownmask(q,L,0) & ~ghostbit(q,L); }
// side to move's pieces that would still count: its piece removed by its own coming placement is excluded
static inline uint32_t stmlive(const int*q,int L){
  uint32_t m=ownmask(q,L,1);
  if(RULE==2 && L==W && !(W&1)) m&=~(1u<<q[0]);
  return m;
}

static void addline(int r0,int c0,int dr,int dc,int len){
  for(int r=r0,c=c0,k=0;k<len;k++,r+=dr,c+=dc) if(r<0||r>=4||c<0||c>=4) return;
  uint32_t m=0; for(int r=r0,c=c0,k=0;k<len;k++,r+=dr,c+=dc) m|=1u<<(r*4+c); lines[nlines++]=m;
}

typedef struct { int tid; int L; uint64_t lo,hi; double cnt[8]; int any; } job_t;
static int PHASE; static int LEVEL;

static void init_range(int L,uint64_t lo,uint64_t hi){
  int q[17],base[17];
  for(uint64_t i=lo;i<hi;i++){
    unrank(i,L,q);
    if(!iscanon(q,L)){ setc(L,i,0); continue; }
    if(L>0 && (haswin(livemask(q,L))||haswin(stmlive(q,L)))){ setc(L,i,0); continue; }
    int BL; int removed=-1;
    if(L==W){ removed=q[0]; for(int j=1;j<L;j++)base[j-1]=q[j]; BL=L-1; } else { for(int j=0;j<L;j++)base[j]=q[j]; BL=L; }
    uint32_t occ=0; for(int j=0;j<BL;j++) occ|=1u<<base[j];
    int n=0,iw=0; uint64_t ks[17]; int cq[17];
    for(int e=0;e<NC;e++){ if(occ>>e&1) continue; if(NOREPLAY&&e==removed) continue;
      base[BL]=e; if(haswin(livemask(base,BL+1))) {iw=1; continue;}
      canon(base,BL+1,cq); uint64_t k=pk(cq,BL+1); int d=1; for(int j=0;j<n;j++) if(ks[j]==k){d=0;break;} if(d) ks[n++]=k; }
    if(iw) setc(L,i,WCUR); else { if(n==0||n>16||(L==W&&n>9)){fprintf(stderr,"bad n %d\n",n);exit(3);} setc(L,i,n); }
  }
}
static void process_range(int L,uint64_t lo,uint64_t hi,job_t*J){
  int q[17],p[17];
  for(uint64_t i=lo;i<hi;i++){
    int c=getc_(L,i);
    if(PHASE==1){ // process current
      if(c!=WCUR && c!=LCUR) continue;
      int win=(c==WCUR);
      unrank(i,L,q);
      // pred list
      uint64_t pi[20]; int pl[20]; uint64_t pks[20]; int np=0; int cq[17];
      if(L>=1){ for(int j=0;j<L-1;j++)p[j]=q[j]; canon(p,L-1,cq); pi[np]=rankq(cq,L-1); pl[np++]=L-1; }
      if(L==W){ uint32_t occ=0; for(int j=0;j<L-1;j++) occ|=1u<<q[j];
        for(int r=0;r<NC;r++){ if(occ>>r&1) continue; if(NOREPLAY&&r==q[L-1]) continue;
          p[0]=r; for(int j=0;j<L-1;j++)p[j+1]=q[j]; canon(p,L,cq); uint64_t k=pk(cq,L);
          int d=1; for(int j=1;j<np;j++) if(pks[j]==k){d=0;break;} if(!d) continue;
          pks[np]=k; pi[np]=rankq(cq,L); pl[np++]=L; } }
      for(int k=0;k<np;k++){
        for(;;){ int pc=getc_(pl[k],pi[k]); if(pc==0||pc>=32) break;
          int nc = win ? (pc==1? LNEXT : pc-1) : WNEXT;
          if(casc(pl[k],pi[k],pc,nc)) break; }
      }
      while(!casc(L,i,c,win?WDONE:LDONE));
    } else { // PHASE 2: next -> cur, count (orbit-weighted = raw counts)
      if(c==0) continue;
      if(!(c==WNEXT||c==LNEXT||c==WCUR||PHASE==3)) continue;
      unrank(i,L,q); double w=orbitsz(q,L);
      if(c==WNEXT){ while(!casc(L,i,c,WCUR)); J->cnt[0]+=w; J->any=1; }
      else if(c==LNEXT){ while(!casc(L,i,c,LCUR)); J->cnt[1]+=w; J->any=1; }
      else if(c==WCUR) J->cnt[0]+=w; // only at level 1 counting
      else if(PHASE==3){ if(c<32) J->cnt[2]+=w, J->cnt[4]++; else J->cnt[3]+=w; J->cnt[5]++; }
    }
  }
}
static int MODEINIT;
static void* worker(void*a){
  job_t*J=a;
  for(int L=0;L<=W;L++){
    uint64_t n=SZ[L]; uint64_t chunk=(n+NT-1)/NT; if(L==W) chunk=(chunk+1)&~1ULL;
    uint64_t lo=chunk*J->tid, hi=lo+chunk; if(lo>n) lo=n; if(hi>n) hi=n;
    if(MODEINIT) init_range(L,lo,hi); else process_range(L,lo,hi,J);
  }
  return NULL;
}
static job_t jobs[64];
static void run(int init,int phase){
  pthread_t th[64]; MODEINIT=init; PHASE=phase;
  for(int t=0;t<NT;t++){ memset(&jobs[t],0,sizeof(job_t)); jobs[t].tid=t; pthread_create(&th[t],0,worker,&jobs[t]); }
  for(int t=0;t<NT;t++) pthread_join(th[t],0);
}
static double now(){ struct timespec ts; clock_gettime(CLOCK_MONOTONIC,&ts); return ts.tv_sec+ts.tv_nsec*1e-9; }

int main(int argc,char**argv){
  W=atoi(argv[1]); MODE=atoi(argv[2]); NT=argc>3?atoi(argv[3]):8; NOREPLAY=argc>4?atoi(argv[4]):0;
  RULE=0; if(argc>5){ if(!strcmp(argv[5],"w0")) RULE=1; else if(!strcmp(argv[5],"g")) RULE=2; else if(strcmp(argv[5],"w1")){fprintf(stderr,"rule must be w1|w0|g\n");return 2;} }
  int len=(MODE==3)?3:4;
  for(int r=0;r<4;r++)for(int c=0;c<4;c++){ addline(r,c,0,1,len); addline(r,c,1,0,len); addline(r,c,1,1,len); addline(r,c,1,-1,len); }
  if(MODE==5) for(int r=0;r<3;r++)for(int c=0;c<3;c++) lines[nlines++]=(1u<<(r*4+c))|(1u<<(r*4+c+1))|(1u<<(r*4+c+4))|(1u<<(r*4+c+5));
  P[0]=1; for(int L=1;L<=16;L++) P[L]=P[L-1]*(NC-L+1);
  P15[0]=1; for(int k=1;k<=15;k++) P15[k]=P15[k-1]*(16-k);
  SZ[0]=1; for(int L=1;L<=16;L++) SZ[L]=3*P15[L-1];
  for(int s=0;s<8;s++)for(int r=0;r<4;r++)for(int c=0;c<4;c++){ int rr=r,cc=c,t; if(s&1)cc=3-cc; if(s&2)rr=3-rr; if(s&4){t=rr;rr=cc;cc=t;} sym[s][r*4+c]=rr*4+cc; }
  for(int L=0;L<=W;L++){ uint64_t sz = (L==W)? (SZ[L]+1)/2 : SZ[L]; arr[L]=calloc(sz,1); if(!arr[L]){fprintf(stderr,"oom\n");return 1;} }
  double t0=now();
  run(1,0);
  fprintf(stderr,"init %.1fs\n",now()-t0);
  // level 1 count
  static double hw[4096],hl[4096]; int level=1;
  int startlev=-1, startwin=0; int openlev[16]; int openwin[16]; for(int c=0;c<16;c++)openlev[c]=-1;
  { run(0,2); double s=0; for(int t=0;t<NT;t++) s+=jobs[t].cnt[0]; hw[1]=s; }
  for(;;){
    // record start / openings resolved at this level
    { int c=getc_(0,0); if(startlev<0 && (c==WCUR||c==LCUR)){startlev=level; startwin=(c==WCUR);} }
    for(int o=0;o<16;o++){ int q[1]={o},cq[1]; canon(q,1,cq); int c=getc_(1,rankq(cq,1)); if(openlev[o]<0 && (c==WCUR||c==LCUR)){openlev[o]=level; openwin[o]=(c==WCUR);} }
    run(0,1);
    run(0,2);
    double sw=0,sl=0; int any=0; for(int t=0;t<NT;t++){ sw+=jobs[t].cnt[0]; sl+=jobs[t].cnt[1]; any|=jobs[t].any; }
    if(!any) break;
    level++; hw[level]=sw; hl[level]=sl;
    fprintf(stderr,"level %d: +%.0f win +%.0f loss (%.1fs)\n",level,sw,sl,now()-t0);
    if(level>4000){fprintf(stderr,"too many levels\n");return 4;}
  }
  run(0,3); double und=0,res=0,cu=0,ct=0; for(int t=0;t<NT;t++){ und+=jobs[t].cnt[2]; res+=jobs[t].cnt[3]; cu+=jobs[t].cnt[4]; ct+=jobs[t].cnt[5]; }
  double tot=und+res, dec=res, acc=0; int med=-1; double w3=hw[1]+hw[2]+hw[3];
  for(int d=1;d<=level;d++){ acc+=hw[d]+hl[d]; if(med<0&&acc>=dec/2) med=d; }
  printf("4x4 W=%d mode=%d noreplay=%d rule=%s lines=%d threads=%d time=%.1fs\n",W,MODE,NOREPLAY,RULE==0?"w1":RULE==1?"w0":"g",nlines,NT,now()-t0);
  printf("reachable nonterminal (raw): %.0f\n",tot);
  printf("canonical states: %.0f, canonical draws %.0f\n",ct,cu);
  printf("draws (raw): %.0f (%.4f%%)\n",und,100*und/tot);
  printf("median forced distance over decisive: %d ; %% positions side-to-move wins in <=3 plies: %.3f%%\n",med,100*w3/tot);
  printf("max distance: %d\n",level);
  if(startlev<0) printf("START: draw\n"); else printf("START: %s in %d plies\n",startwin?"X wins":"O wins",startlev);
  int xw=0,xl=0,dr=0;
  for(int o=0;o<16;o++){ if(openlev[o]<0){printf(" open %2d (r%d c%d): draw\n",o,o/4,o%4);dr++;}
    else { printf(" open %2d (r%d c%d): %s %d\n",o,o/4,o%4,openwin[o]?"X loses":"X wins",openlev[o]); if(openwin[o])xl++; else xw++; } }
  printf("PIE: openings X-win %d, X-loss %d, draw %d -> %s\n",xw,xl,dr, dr?"pie game is a DRAW (X picks a drawn opening)":"O (chooser) wins under pie");
  printf("hist(level:win,loss):"); for(int d=1;d<=level;d++) printf(" %d:%.0f,%.0f",d,hw[d],hl[d]); printf("\n");
  return 0;
}
