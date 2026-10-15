#!/bin/sh
# Regression check for the vanishing-piece rule flags in psolve_sym.c and check-4x4/vi.c.
# Asserts known values recorded in ../results-4x4.txt and that the two solvers agree.
# Usage: sh regress.sh [threads] [bin-dir]     (~2 minutes, ~1 GB RAM)
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
NT=${1:-8}; B=${2:-${TMPDIR:-/tmp}}
cc -O3 -o "$B/psolve_sym" "$HERE/psolve_sym.c" -lpthread 2>/dev/null
cc -O3 -o "$B/vi4" "$HERE/../check-4x4/vi.c" -lpthread 2>/dev/null
fail=0
expect(){ # name, output, grep-pattern
  if printf '%s\n' "$2" | grep -q -- "$3"; then echo "ok   $1"; else echo "FAIL $1: expected '$3'"; fail=1; fi; }
# legacy W=8, vacated blocked: X wins in 29 (openings 28/36/58)
a=$("$B/psolve_sym" 8 5 "$NT" 1 w1 2>/dev/null)
expect "psolve W8 m5 nr1 w1 start" "$a" "START: X wins in 29 plies"
expect "psolve W8 m5 nr1 w1 corner" "$a" "open  0 (r0 c0): X wins 58"
# w0 excludes NextToVanish(after placement) only when it is the mover's: never for even W -> identical
b=$("$B/psolve_sym" 8 5 "$NT" 1 w0 2>/dev/null)
[ "$(printf '%s\n' "$a" | sed 1d)" = "$(printf '%s\n' "$b" | sed 1d)" ] && echo "ok   psolve W8 w0 == w1" || { echo "FAIL psolve W8 w0 != w1"; fail=1; }
# ghost reading at W=8: only 3 live pieces, a 4-cell shape is impossible
c=$("$B/psolve_sym" 8 5 "$NT" 1 g 2>/dev/null)
expect "psolve W8 m5 g all draws" "$c" "draws (raw): 582913217 (100.0000%)"
# independent checker agrees on raw counts
v=$("$B/vi4" 8 5 1 "$NT" w0 2>/dev/null)
expect "vi W8 m5 nr1 w0 start" "$v" "START: X wins in 29"
expect "vi W8 m5 nr1 w0 counts" "$v" "reachable nonterminal raw 571556225"
expect "vi W8 m5 nr1 w0 draws" "$v" "draws 2489040 (0.4355%)"
# W=7 (odd): legacy is the old 'best candidate'; under w0 the mover keeps 3 live pieces -> all draws
d=$("$B/vi4" 7 5 0 "$NT" w1 2>/dev/null)
expect "vi W7 m5 w1 start" "$d" "START: X wins in 31"
expect "vi W7 m5 w1 corner draw" "$d" "open  0: draw 0"
e=$("$B/vi4" 7 5 0 "$NT" w0 2>/dev/null)
expect "vi W7 m5 w0 all draws" "$e" "draws 63994817 (100.0000%)"
[ $fail = 0 ] && echo "ALL OK" || { echo "FAILURES"; exit 1; }
