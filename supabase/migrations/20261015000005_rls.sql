-- 0005: RLS on, no anon/authenticated policies. The server uses the service role (§9, D18).
alter table players enable row level security;
alter table ratings enable row level security;
alter table games enable row level security;
alter table game_actions enable row level security;
alter table daily_matches enable row level security;
alter table friend_requests enable row level security;
alter table friendships enable row level security;
alter table blocks enable row level security;
alter table direct_messages enable row level security;
alter table streaks enable row level security;
alter table quest_days enable row level security;
alter table challenges enable row level security;
alter table bot_keys enable row level security;
alter table reward_grants enable row level security;
alter table seasons enable row level security;
alter table master_season_results enable row level security;
