-- 0002: games, actions, live daily matches (§9)
create table games (
  id uuid primary key, mode text not null, time_control text not null, rated boolean not null,
  rules_id text not null, rules jsonb not null,           -- server stores {rules, seatA, seatB, rematchOf, challengeCode}
  seed bigint not null,                                  -- ulong stored as unchecked((long)seed)
  seat_a uuid not null references players(id), seat_b uuid not null references players(id),
  swapped boolean not null default false, origin text not null,
  outcome text not null, end_reason text not null, winner_seat smallint, win_line smallint[] not null default '{}',
  detail text, started_at timestamptz not null, ended_at timestamptz not null,
  rating_snapshots jsonb not null default '[]');
create index games_seat_a on games (seat_a, ended_at desc);
create index games_seat_b on games (seat_b, ended_at desc);

create table game_actions (                               -- append-only; shared by finished games and live daily matches
  game_id uuid not null, ply int not null, seat smallint not null, kind text not null, cell smallint,
  think_ms int not null, auto boolean not null default false, vanished_cell smallint, at timestamptz not null,
  primary key (game_id, ply));

create table daily_matches (                              -- live correspondence state
  id uuid primary key, setup jsonb not null, version bigint not null default 0,
  turn_deadline timestamptz, ended boolean not null default false, started_at timestamptz not null,
  seat_a uuid not null, seat_b uuid not null);
create index daily_due on daily_matches (turn_deadline) where not ended;
create index daily_a on daily_matches (seat_a) where not ended;
create index daily_b on daily_matches (seat_b) where not ended;
