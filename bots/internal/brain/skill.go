// Package brain decides bot moves and tunes difficulty so bots win ≈49% of the time.
package brain

import (
	"encoding/json"
	"math"
	"os"
	"sync"
)

// TargetWinRate is the long-run share of decided games a bot should win against each opponent.
const TargetWinRate = 0.49

// Step size of the adaptive staircase. After a bot win skill drops by stepDown, after a loss it rises by stepUp;
// the staircase settles where P(win)·stepDown = P(loss)·stepUp, i.e. P(win) = stepUp/(stepUp+stepDown) = TargetWinRate.
const (
	stepScale = 0.10
	stepDown  = stepScale * (1 - TargetWinRate)
	stepUp    = stepScale * TargetWinRate
)

// Shadow pool (anti-cheat, owner decision 2026-10-16): flagged players meet bots at full strength. The staircase then
// aims at a bot win rate of ShadowTarget, which keeps the flagged player's win rate inside 40-70%: they keep progressing
// slowly, but no longer farm wins. Against perfect play the colour decides, so even a perfect bot only reaches ~50%.
const (
	ShadowTarget    = 0.45
	shadowBaseSkill = 0.97
	shadowStepDown  = stepScale * (1 - ShadowTarget)
	shadowStepUp    = stepScale * ShadowTarget
)

// BaseSkill maps an opponent rating to the probability of playing the perfect (tablebase) move.
func BaseSkill(rating int) float64 {
	p := 0.30 + 0.62/(1+math.Exp(-float64(rating-1400)/260))
	return clamp(p, 0.25, 0.97)
}

// Tracker remembers, per opponent, how far to shift the base skill. Safe for concurrent sessions.
type Tracker struct {
	mu            sync.Mutex
	Offsets       map[string]float64 `json:"offsets"`
	ShadowOffsets map[string]float64 `json:"shadowOffsets"`
	path          string
}

// NewTracker loads persisted offsets from path ("" = memory only).
func NewTracker(path string) *Tracker {
	t := &Tracker{Offsets: map[string]float64{}, ShadowOffsets: map[string]float64{}, path: path}
	if path != "" {
		if raw, err := os.ReadFile(path); err == nil {
			_ = json.Unmarshal(raw, t)
			if t.Offsets == nil {
				t.Offsets = map[string]float64{}
			}
			if t.ShadowOffsets == nil {
				t.ShadowOffsets = map[string]float64{}
			}
		}
	}
	return t
}

// Skill for a given opponent.
func (t *Tracker) Skill(opponent string, rating int) float64 {
	return t.SkillFor(opponent, rating, false)
}

// SkillFor a given opponent; shadow-pool opponents get the full-strength profile.
func (t *Tracker) SkillFor(opponent string, rating int, shadow bool) float64 {
	t.mu.Lock()
	defer t.mu.Unlock()
	if shadow {
		return clamp(shadowBaseSkill+t.ShadowOffsets[opponent], 0.5, 0.99)
	}
	return clamp(BaseSkill(rating)+t.Offsets[opponent], 0.05, 0.99)
}

// Record a finished game. Draws/aborts don't move the staircase.
func (t *Tracker) Record(opponent string, botWon, botLost bool) {
	t.RecordFor(opponent, botWon, botLost, false)
}

// RecordFor a finished game against a normal or shadow-pool opponent.
func (t *Tracker) RecordFor(opponent string, botWon, botLost, shadow bool) {
	t.mu.Lock()
	offsets, down, up := t.Offsets, stepDown, stepUp
	if shadow {
		offsets, down, up = t.ShadowOffsets, shadowStepDown, shadowStepUp
	}
	switch {
	case botWon:
		offsets[opponent] = clamp(offsets[opponent]-down, -0.7, 0.5)
	case botLost:
		offsets[opponent] = clamp(offsets[opponent]+up, -0.7, 0.5)
	}
	t.mu.Unlock()
	t.save()
}

func (t *Tracker) save() {
	if t.path == "" {
		return
	}
	t.mu.Lock()
	raw, err := json.Marshal(t)
	t.mu.Unlock()
	if err == nil {
		_ = os.WriteFile(t.path, raw, 0o600)
	}
}

func clamp(v, lo, hi float64) float64 { return math.Max(lo, math.Min(hi, v)) }
