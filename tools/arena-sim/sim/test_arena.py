import random
import unittest
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from arena import Rules, apply, legal, play, get_bot, Bot, popcount


def bits(cells):
    m = 0
    for c in cells:
        m |= 1 << c
    return m


def run(R, seq, s=None):
    s = s or R.start()
    res = None
    for c in seq:
        assert c in legal(R, s), (c, legal(R, s))
        res = apply(R, s, c)
        s = res[0]
    return s, res


def board_str(s, n=3):
    out = []
    for c in range(n * n):
        out.append("X" if (s[0] >> c) & 1 else "O" if (s[1] >> c) & 1 else ".")
    return "".join(out)


class BaseRuleCases(unittest.TestCase):
    """Hand-verified cases from docs/ARCHITECTURE.md (default ruleset w0)."""

    def setUp(self):
        self.R = Rules()

    def test_case_ply7(self):
        s, _ = run(self.R, [4, 0, 8, 2, 6, 3, 5])
        self.assertEqual(board_str(s), "O.OOXXX.X")
        self.assertEqual(s[7], 1)
        self.assertEqual(s[2][0], 4)
        self.assertEqual(sorted(legal(self.R, s)), [1, 4, 7])

    def test_ghost_blocks_O(self):
        s, (s2, n, d, u) = run(self.R, [4, 0, 8, 2, 6, 3, 5, 1])
        self.assertEqual(n, 0)
        self.assertEqual(board_str(s), "OOOO.XX.X")
        self.assertEqual(s[2][0], 0)
        self.assertEqual(sorted(legal(self.R, s)), [0, 4, 7])
        self.assertEqual((s[3], s[4]), (100, 100))

    def test_ghost_blocks_X(self):
        s, (_, n, d, u) = run(self.R, [4, 0, 8, 2, 6, 3, 5, 1, 7])
        self.assertEqual(n, 0)
        self.assertEqual(board_str(s), ".OOO.XXXX")
        self.assertEqual(sorted(legal(self.R, s)), [0, 4, 8])

    def test_ghost_off_counts(self):
        R = Rules(ghost=False)
        # with ghost off, O's 0-1-2 already counts at ply 8
        s, (_, n, d, u) = run(R, [4, 0, 8, 2, 6, 3, 5, 1])
        self.assertEqual(n, 1)
        self.assertEqual(u, bits([0, 1, 2]))
        self.assertEqual(s[3], 75)  # X damaged

    def test_vacated_replay(self):
        s, _ = run(self.R, [4, 0, 8, 2, 6, 3, 5, 4])
        self.assertEqual(board_str(s), "O.OOOXX.X")
        self.assertEqual(sorted(legal(self.R, s)), [0, 1, 7])

    def test_vacated_not_playable(self):
        R = Rules(vacated=False)
        s, _ = run(R, [4, 0, 8, 2, 6, 3, 5])
        self.assertNotIn(4, legal(R, s))


class ArenaRules(unittest.TestCase):
    def setUp(self):
        self.R = Rules()

    def test_shatter_removes_from_board_and_queue(self):
        s, (_, n, d, u) = run(self.R, [0, 4, 1, 8, 2])
        self.assertEqual(n, 1)
        self.assertEqual(d, 25)
        self.assertEqual(board_str(s), "....O...O")
        self.assertEqual(s[2], (4, 8))  # FIFO order of survivors preserved
        self.assertEqual((s[3], s[4]), (100, 75))
        self.assertEqual(s[5], 1)  # X streak
        self.assertEqual(s[7], 1)  # O to move

    def test_shatter_keeps_order_mid_queue(self):
        # X:3 O:0 X:6 O:1 X:4 O:8 X:5 -> X line 3-4-5 shatters, queue keeps 0,6,1,8
        s, (_, n, d, u) = run(Rules(W=9), [3, 0, 6, 1, 4, 8, 5])
        self.assertEqual(n, 1)
        self.assertEqual(s[2], (0, 6, 1, 8))

    def test_queue_not_full_after_shatter_means_no_ghost(self):
        # after shatter queue shorter than W so no piece vanishes on next placement
        s, _ = run(self.R, [0, 4, 1, 8, 2])
        s2, n, d, u = apply(self.R, s, 3)
        self.assertEqual(s2[2], (4, 8, 3))

    def test_combo_and_reset(self):
        R = Rules(W=9)
        s = (bits([0, 1, 3, 4]), bits([6, 8]), (0, 6, 1, 8, 3, 4), 100, 100, 0, 0, 0)
        s, n, d, u = apply(R, s, 2)
        self.assertEqual((n, d, s[5], s[4]), (1, 25, 1, 75))
        s, n, d, u = apply(R, s, 7)  # O: 6-7-8 completes O line
        # O scored: that is fine, O streak 1
        self.assertEqual((n, d, s[6], s[3]), (1, 25, 1, 75))
        s, n, d, u = apply(R, s, 5)  # X 3-4-5
        self.assertEqual((n, d, s[5], s[4]), (1, 35, 2, 40))
        s, n, d, u = apply(R, s, 0)  # O no line -> O streak resets
        self.assertEqual((n, s[6]), (0, 0))
        s, n, d, u = apply(R, s, 1)  # X no line -> streak resets
        self.assertEqual((n, s[5]), (0, 0))

    def test_combo_ramp_caps(self):
        R = Rules(ramp=(10, 20, 30))
        s = (bits([0, 1]), 0, (0, 1), 100, 100, 5, 0, 0)
        s, n, d, u = apply(R, s, 2)
        self.assertEqual((d, s[5]), (30, 6))

    def test_crit_double_line(self):
        R = Rules(W=9)
        s = (bits([0, 1, 4, 6]), bits([3, 5]), (0, 3, 1, 5, 4, 6), 100, 100, 0, 0, 0)
        s2, n, d, u = apply(R, s, 2)
        self.assertEqual(n, 2)
        self.assertEqual(d, 2 * 25 + 15)
        self.assertEqual(u, bits([0, 1, 2, 4, 6]))
        self.assertEqual(s2[2], (3, 5))
        self.assertEqual(s2[4], 35)

    def test_ghost_blocks_one_line_of_crit(self):
        R7 = Rules(W=7)
        # queue has 6 -> after X places 2 it is full (7 == W); front 0 is X's ghost,
        # so 0-1-2 is excluded and only 2-4-6 counts
        s = (bits([0, 1, 4, 6]), bits([3, 5]), (0, 3, 1, 5, 4, 6), 100, 100, 0, 0, 0)
        s2, n, d, u = apply(R7, s, 2)
        self.assertEqual(n, 1)
        self.assertEqual(u, bits([2, 4, 6]))

    def test_no_counted_line_left_after_move(self):
        """Invariant: checking only lines through the placed cell is complete."""
        for cfg in (Rules(), Rules(ghost=False), Rules(W=9), Rules(n=4, k=3, W=7), Rules(W=5),
                    Rules(knockback=1, combo="unanswered")):
            rng = random.Random(1)
            for g in range(300):
                s = cfg.start(hp=10 ** 6)
                for ply in range(60):
                    c = rng.choice(legal(cfg, s))
                    m = s[7]
                    s, n, d, u = apply(cfg, s, c)
                    mine = s[0] if m == 0 else s[1]
                    gb = 0
                    if cfg.ghost and len(s[2]) == cfg.W and (mine >> s[2][0]) & 1:
                        gb = 1 << s[2][0]
                    for L in cfg.lines:
                        self.assertFalse(mine & L == L and not L & gb)
                    # queue and board agree
                    self.assertEqual(bits(s[2]), s[0] | s[1])
                    self.assertEqual(len(set(s[2])), len(s[2]))
                    self.assertLessEqual(len(s[2]), cfg.W)
                    self.assertFalse(s[0] & s[1])


class VariantRules(unittest.TestCase):
    def test_unanswered_combo(self):
        R = Rules(W=9, combo="unanswered")
        s = (bits([0, 1, 3, 4]), bits([7]), (0, 7, 1, 3, 4), 100, 100, 0, 0, 0)
        s, n, d, u = apply(R, s, 2)            # X line, streak 1
        self.assertEqual((d, s[5]), (25, 1))
        s, n, d, u = apply(R, s, 8)            # O blank
        s, n, d, u = apply(R, s, 6)            # X blank: does NOT reset
        self.assertEqual(s[5], 1)
        s, n, d, u = apply(R, s, 0)            # O blank
        s, n, d, u = apply(R, s, 5)            # X 3-4-5: unanswered 2nd line
        self.assertEqual((n, d, s[5]), (1, 35, 2))

    def test_unanswered_reset_by_opponent_line(self):
        R = Rules(W=9, combo="unanswered")
        s = (bits([0, 1]), bits([6, 7]), (0, 6, 1, 7), 100, 100, 3, 0, 1)
        s, n, d, u = apply(R, s, 8)            # O scores -> X streak reset
        self.assertEqual((n, s[5], s[6]), (1, 0, 1))

    def test_knockback_removes_victims_oldest(self):
        R = Rules(W=9, knockback=1)
        s = (bits([0, 1]), bits([4, 8, 6]), (4, 0, 8, 1, 6), 100, 100, 0, 0, 0)
        s2, n, d, u = apply(R, s, 2)
        self.assertEqual(n, 1)
        self.assertEqual(s2[2], (8, 6))        # O's oldest (4) knocked out
        self.assertEqual(s2[1], bits([8, 6]))


class ClockRules(unittest.TestCase):
    def test_equal_turns_and_end_types(self):
        R = Rules()
        rng = random.Random(7)
        for i in range(400):
            r = play(R, get_bot("random"), get_bot("random"), rng, T=30)
            if r["end"] in ("timeout", "draw"):
                self.assertEqual(r["movesX"], r["movesO"])
                self.assertGreaterEqual(r["t"], 30)
            if r["end"] == "KO":
                self.assertTrue(r["hpX"] <= 0 or r["hpO"] <= 0)
                self.assertEqual(r["winner"], 0 if r["hpO"] <= 0 else 1)
            if r["end"] == "timeout":
                self.assertNotEqual(r["hpX"], r["hpO"])
                self.assertEqual(r["winner"], 0 if r["hpX"] > r["hpO"] else 1)

    def test_O_gets_last_move(self):
        R = Rules(ramp=(0,))  # no damage at all -> always ties
        rng = random.Random(3)
        r = play(R, get_bot("random"), get_bot("random"), rng, T=10, overtime=False)
        self.assertEqual(r["end"], "draw")
        self.assertEqual(r["movesX"], r["movesO"])

    def test_overtime_next_line_wins(self):
        R = Rules(ramp=(0,), crit_bonus=0)
        rng = random.Random(5)
        ends = {}
        for i in range(200):
            r = play(R, get_bot("greedy"), get_bot("random"), rng, T=20)
            ends[r["end"]] = ends.get(r["end"], 0) + 1
            if r["end"] == "overtime":
                self.assertIn(r["winner"], (0, 1))
                self.assertGreater(r["linesX"] + r["linesO"], 0)
                # winner made the last line
            self.assertNotEqual(r["end"], "timeout")
            self.assertNotEqual(r["end"], "KO")
        self.assertGreater(ends.get("overtime", 0), 150)

    def test_red_alert_multiplier(self):
        R = Rules()
        rng = random.Random(11)
        seen = 0
        for i in range(300):
            r = play(R, get_bot("greedy"), get_bot("greedy"), rng, T=40, red_mult=2.0, red_secs=1000, record=True)
            for (m, c, n, d, hx, ho, t) in r["trace"]:
                pass
            # every line in red alert deals double damage: total damage = 2x lines x 25 (no combos/crits)
            if r["critsX"] + r["critsO"] == 0 and max(r["maxStreakX"], r["maxStreakO"]) <= 1 and r["end"] != "overtime":
                self.assertEqual(r["dmgX"], 50 * r["linesX"])
                seen += 1
        self.assertGreater(seen, 50)

    def test_ko_reply_equal_turns(self):
        R = Rules(hp=50)
        rng = random.Random(4)
        n_ko = 0
        for i in range(400):
            r = play(R, get_bot("greedy"), get_bot("greedy"), rng, T=120, ko_reply=True)
            if r["end"] == "KO":
                n_ko += 1
                self.assertEqual(r["movesX"], r["movesO"])  # KO never cuts O's last turn
        self.assertGreater(n_ko, 100)

    def test_ko_ends_immediately(self):
        R = Rules(hp=25)
        rng = random.Random(9)
        for i in range(100):
            r = play(R, get_bot("greedy"), get_bot("greedy"), rng, T=120)
            if r["end"] == "KO":
                self.assertEqual(r["linesX"] + r["linesO"], 1)


class Bots(unittest.TestCase):
    def test_mm_takes_win_and_blocks(self):
        R = Rules()
        # X has 0,1; X to move: must take 2
        s = (bits([0, 1]), bits([4]), (0, 4, 1), 25, 25, 0, 0, 0)
        self.assertEqual(Bot("mm", 2).move(R, s, random.Random(0)), 2)
        # O to move, X threatens 0-1-2, O has no win: O must block 2
        s = (bits([0, 1]), bits([4]), (0, 4, 1), 100, 100, 0, 0, 1)
        for seed in range(5):
            self.assertEqual(Bot("mm", 2).move(R, s, random.Random(seed)), 2)


if __name__ == "__main__":
    unittest.main()
