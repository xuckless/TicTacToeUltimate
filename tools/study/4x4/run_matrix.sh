#!/bin/sh
# Re-runs the 4x4 study matrix with psolve_sym and writes ../results-4x4.txt.
# Usage: sh run_matrix.sh [threads] [outdir-for-binary]
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
NT=${1:-8}
BIN=${2:-${TMPDIR:-/tmp}}/psolve_sym
cc -O3 -o "$BIN" "$HERE/psolve_sym.c" -lpthread
OUT="$HERE/../results-4x4.txt"
: > "$OUT"
for MODE in 5 3 4; do for W in 8 9; do for NR in 1 0; do for RULE in w1 w0 g; do
  # w0 == g for odd W, and w0 == w1 for even W (see psolve_sym.c header); run them anyway as a check
  echo "=== psolve_sym W=$W mode=$MODE noreplay=$NR rule=$RULE" >> "$OUT"
  "$BIN" "$W" "$MODE" "$NT" "$NR" "$RULE" 2>/dev/null >> "$OUT"
done; done; done; done
