// Package protocol holds the bot WebSocket messages (spec/bot-protocol.md, protocol v1).
package protocol

// Envelope is used to read the "type" discriminator first.
type Envelope struct {
	Type string `json:"type"`
}

// Hello from the server.
type Hello struct {
	BotID   string `json:"botId"`
	BotName string `json:"botName"`
}

// Rules subset of RuleSetDto.
type Rules struct {
	ID string `json:"id"`
}

// Opponent (PlayerCardDto subset).
type Opponent struct {
	UserID      string `json:"userId"`
	DisplayName string `json:"displayName"`
	Rating      *int   `json:"rating"`
}

// GameStart from the server.
type GameStart struct {
	MatchID     string   `json:"matchId"`
	Mode        string   `json:"mode"`
	TimeControl string   `json:"timeControl"`
	Rules       Rules    `json:"rules"`
	Seat        int      `json:"seat"`
	You         string   `json:"you"`
	Opponent    Opponent `json:"opponent"`
	// OpponentShadow: the opponent is in the anti-cheat shadow pool (play at full strength, see brain.ShadowTarget).
	OpponentShadow bool `json:"opponentShadow"`
}

// State from the server.
type State struct {
	MatchID      string `json:"matchId"`
	Ply          int    `json:"ply"`
	Phase        string `json:"phase"`
	ToMove       string `json:"toMove"`
	You          string `json:"you"`
	YourTurn     bool   `json:"yourTurn"`
	Board        string `json:"board"`
	Actions      string `json:"actions"`
	NextToVanish *int   `json:"nextToVanish"`
	Legal        []int  `json:"legal"`
	AllowanceMs  *int   `json:"allowanceMs"`
}

// GameEnd from the server.
type GameEnd struct {
	MatchID string `json:"matchId"`
	Outcome string `json:"outcome"`
	Reason  string `json:"reason"`
	YouWon  bool   `json:"youWon"`
}

// Ping from the server.
type Ping struct {
	T int64 `json:"t"`
}

// Error from the server.
type Error struct {
	ID      string `json:"id"`
	Code    string `json:"code"`
	Message string `json:"message"`
}

// Seek (bot → server).
type Seek struct {
	Type               string   `json:"type"`
	ID                 string   `json:"id,omitempty"`
	TimeControls       []string `json:"timeControls"`
	MaxConcurrentGames int      `json:"maxConcurrentGames"`
}

// Action (bot → server).
type Action struct {
	Type    string `json:"type"`
	ID      string `json:"id"`
	MatchID string `json:"matchId"`
	Ply     int    `json:"ply"`
	Kind    string `json:"kind"`
	Cell    *int   `json:"cell,omitempty"`
}

// Emote (bot → server).
type Emote struct {
	Type    string `json:"type"`
	ID      string `json:"id,omitempty"`
	MatchID string `json:"matchId"`
	EmoteID string `json:"emoteId"`
}

// OpponentEmote from the server.
type OpponentEmote struct {
	MatchID string `json:"matchId"`
	Seat    int    `json:"seat"`
	EmoteID string `json:"emoteId"`
}

// Simple (bot → server): unseek, pong.
type Simple struct {
	Type string `json:"type"`
	ID   string `json:"id,omitempty"`
	T    int64  `json:"t,omitempty"`
}
