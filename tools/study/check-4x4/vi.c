/* Independent checker: no symmetry, direct partial-permutation ranking,
   forward reachability sweep, then level-synchronous value iteration
   (not counter-based retrograde). usage: vi W mode noreplay threads [rule]
   rule: w1 (default, legacy), w0 (exclude the mover's piece that vanishes on the very next
   ply; only possible for odd W), g (exclude the mover's piece that vanishes within the next
   two plies). Written independently of psolve_sym's version: here the ghost is found by
   simulating the next two removals instead of by queue-position formulas.
   mode 3 = 3-in-row, 4 = 4-in-row, 5 = 4-in-row or 2x2 square. */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <pthread.h>
typedef uint64_t u64;
static int W, MODE, NOREP, NT, RULE;
static uint32_t lines[64]; static int nl;
static u64 off[17], cnt[17];
static uint8_t *val;   /* 0 undecided; else dist+1; dist even => mover loses */
static uint8_t *reach; /* bit per state */
static uint8_t *term;  /* bit per state */
static u64 TOT;
#define GB(a,i) ((a[(i)>>3]>>((i)&7))&1)
static inline void SB(uint8_t*a,u64 i){ __atomic_fetch_or(&a[i>>3],(uint8_t)(1u<<(i&7)),__ATOMIC_RELAXED); }

static void mklines(void){
  int L = MODE==3?3:4;
  int dr[4]={0,1,1,1}, dc[4]={1,0,1,-1};
  for(int r=0;r<4;r++)for(int c=0;c<4;c++)for(int d=0;d<4;d++){
    int er=r+dr[d]*(L-1), ec=c+dc[d]*(L-1);
    if(er<0||er>3||ec<0||ec>3) continue;
    uint32_t m=0; for(int k=0;k<L;k++) m|=1u<<((r+dr[d]*k)*4+c+dc[d]*k);
    lines[nl++]=m;}
  if(MODE==5) for(int r=0;r<3;r++)for(int c=0;c<3;c++)
    lines[nl++]=(1u<<(r*4+c))|(1u<<(r*4+c+1))|(1u<<(r*4+c+4))|(1u<<(r*4+c+5));
}
static u64 rank_(const int*q,int k){
  u64 x=0; uint32_t used=0;
  for(int i=0;i<k;i++){ int d=q[i]-__builtin_popcount(used&((1u<<q[i])-1)); x=x*(16-i)+d; used|=1u<<q[i]; }
  return off[k]+x;
}
static int unrank_(u64 idx,int*q){
  int k=0; while(k<W && idx>=off[k+1]) k++;
  u64 x=idx-off[k]; int dig[16];
  for(int i=k-1;i>=0;i--){ dig[i]=x%(16-i); x/=(16-i); }
  uint32_t used=0;
  for(int i=0;i<k;i++){ int d=dig[i]; for(int c=0;c<16;c++) if(!(used>>c&1)){ if(d==0){q[i]=c;break;} d--; } used|=1u<<q[i]; }
  return k;
}
static int lastwon(const int*q,int k){
  if(k==0) return 0; uint32_t m=0;
  for(int i=k-1;i>=0;i-=2) m|=1u<<q[i];
  if(RULE){ /* simulate removals over the next RULE plies (w0: 1, g: 2) */
    int len=k, head=0;
    for(int p=0;p<(RULE==1?1:2);p++){
      if(len==W){ int idx=head; if(((k-1-idx)&1)==0) m&=~(1u<<q[idx]); head++; }
      else len++; /* placement without removal grows the queue */
    }
  }
  for(int j=0;j<nl;j++) if((m&lines[j])==lines[j]) return 1;
  return 0;
}
/* children; returns count */
static int kids(const int*q,int k,u64*out){
  int b[16]; int nb=0, start=(k==W)?1:0; uint32_t occ=0;
  for(int i=start;i<k;i++){ b[nb++]=q[i]; occ|=1u<<q[i]; }
  int n=0;
  for(int c=0;c<16;c++){ if(occ>>c&1) continue; if(NOREP && k==W && c==q[0]) continue;
    b[nb]=c; out[n++]=rank_(b,nb+1); }
  return n;
}
/* ---- parallel helpers ---- */
static int phase, level; static u64 changed[64];
static uint8_t *front, *nfront;
static void* work(void*a){
  int t=(int)(intptr_t)a; u64 lo=TOT*t/NT, hi=TOT*(t+1)/NT; lo&=~(u64)63; if(t==NT-1) hi=TOT; else hi&=~(u64)63;
  int q[16]; u64 ch[16]; u64 nc=0;
  for(u64 i=lo;i<hi;i++){
    if(phase==0){ /* expand frontier */
      if(!GB(front,i)) { if((i&7)==7 || 1){ } continue; }
      int k=unrank_(i,q);
      if(lastwon(q,k)){ SB(term,i); continue; }
      int n=kids(q,k,ch);
      for(int j=0;j<n;j++) if(!GB(reach,ch[j])){ SB(nfront,ch[j]); }
    } else { /* value iteration level */
      if(!GB(reach,i)||GB(term,i)||val[i]) continue;
      int k=unrank_(i,q); int n=kids(q,k,ch);
      if(level&1){ for(int j=0;j<n;j++) if(val[ch[j]]==(uint8_t)level){ val[i]=level+1; nc++; break; } }
      else { int ok=1, hit=0; for(int j=0;j<n;j++){ int v=val[ch[j]]; if(v==0||v>level||!((v-1)&1)){ok=0;break;} if(v==level) hit=1; }
        if(ok&&hit){ val[i]=level+1; nc++; } }
    }
  }
  changed[t]=nc; return 0;
}
static void run(void){ pthread_t th[64]; for(int t=0;t<NT;t++) pthread_create(&th[t],0,work,(void*)(intptr_t)t); for(int t=0;t<NT;t++) pthread_join(th[t],0); }
int main(int argc,char**argv){
  W=atoi(argv[1]); MODE=atoi(argv[2]); NOREP=atoi(argv[3]); NT=atoi(argv[4]);
  RULE=0; if(argc>5){ if(!strcmp(argv[5],"w0")) RULE=1; else if(!strcmp(argv[5],"g")) RULE=2; }
  mklines();
  off[0]=0; cnt[0]=1; for(int k=1;k<=16;k++) cnt[k]=cnt[k-1]*(16-k+1);
  for(int k=1;k<=W+1;k++) off[k]=off[k-1]+cnt[k-1];
  TOT=off[W+1]; u64 B=(TOT+7)/8+8;
  fprintf(stderr,"W=%d mode=%d norep=%d lines=%d total=%llu\n",W,MODE,NOREP,nl,(unsigned long long)TOT);
  reach=calloc(B,1); term=calloc(B,1); front=calloc(B,1); nfront=calloc(B,1);
  SB(reach,0); SB(front,0);
  phase=0; int sweeps=0;
  for(;;){ memset(nfront,0,B); run(); sweeps++;
    u64 any=0; for(u64 w=0;w<B;w++){ nfront[w]&=~reach[w]; reach[w]|=nfront[w]; any|=nfront[w]; }
    uint8_t*t=front; front=nfront; nfront=t; if(!any) break; }
  free(front); free(nfront);
  u64 nreach=0,nterm=0; for(u64 w=0;w<B;w++){ nreach+=__builtin_popcount(reach[w]); nterm+=__builtin_popcount(term[w]); }
  fprintf(stderr,"reach sweeps %d reachable %llu terminal %llu\n",sweeps,(unsigned long long)nreach,(unsigned long long)nterm);
  val=calloc(TOT,1);
  for(u64 i=0;i<TOT;i++) if(GB(term,i)) val[i]=1;
  phase=1; int quiet=0, maxd=0;
  for(level=1;level<250;level++){ run(); u64 s=0; for(int t=0;t<NT;t++) s+=changed[t];
    if(s){ maxd=level; quiet=0; } else if(++quiet==2) break; }
  /* stats over reachable nonterminal */
  u64 nonterm=nreach-nterm, draws=0, le3=0; u64 hist[256]={0};
  for(u64 i=0;i<TOT;i++){ if(!GB(reach,i)||GB(term,i)) continue; int v=val[i];
    if(!v) draws++; else { hist[v-1]++; if(((v-1)&1) && v-1<=3) le3++; } }
  u64 dec=nonterm-draws, acc=0; int med=-1;
  for(int d=0;d<256;d++){ acc+=hist[d]; if(acc*2>=dec){ med=d; break; } }
  printf("W=%d mode=%d noreplay=%d rule=%s\nreachable nonterminal raw %llu (terminal %llu)\n",W,MODE,NOREP,RULE==0?"w1":RULE==1?"w0":"g",(unsigned long long)nonterm,(unsigned long long)nterm);
  printf("draws %llu (%.4f%%)  median %d  <=3 %.4f%%  maxdist %d\n",(unsigned long long)draws,100.0*draws/nonterm,med,100.0*le3/nonterm,maxd);
  int v=val[0]; printf("START: %s %d\n", !v?"draw":((v-1)&1)?"X wins in":"O wins in", v?v-1:0);
  for(int c=0;c<16;c++){ int q1[1]={c}; int vv=val[rank_(q1,1)];
    printf(" open %2d: %s %d\n",c,!vv?"draw":((vv-1)&1)?"X loses":"X wins",vv?vv-1:0); }
  printf("hist:"); for(int d=1;d<256;d++) if(hist[d]) printf(" %d:%llu",d,(unsigned long long)hist[d]); printf("\n");
}
