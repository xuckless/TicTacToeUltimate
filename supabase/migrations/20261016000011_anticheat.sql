-- 0011: anti-cheat. Per player per game: how many critical positions they faced (some move would have thrown the result,
-- by the solved tablebase), how many they got right, and their average think time there. The rolling numbers put suspicious
-- players in the shadow pool (players.shadow_pool, 0010): matched with strong bots or each other, never banned automatically.
create table game_metrics (
  game_id uuid not null,
  user_id uuid not null,
  critical int not null,
  accurate int not null,
  think_ms_avg int not null,
  won boolean not null,
  at timestamptz not null,
  primary key (game_id, user_id)
);
create index game_metrics_user on game_metrics (user_id, at desc);
alter table game_metrics enable row level security;
