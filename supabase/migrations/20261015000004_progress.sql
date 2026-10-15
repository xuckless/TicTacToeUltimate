-- 0004: streaks, quests, challenges, bot keys, rewards, seasons (§9)
create table streaks (user_id uuid primary key, current int not null default 0, best int not null default 0,
  last_played_day date, freezes smallint not null default 0 check (freezes between 0 and 2));
create table quest_days (user_id uuid not null, day date not null, wins int not null default 0,
  freeze_awarded boolean not null default false, bonus_target int, bonus_claimed boolean not null default false,
  primary key (user_id, day));

create table challenges (code text primary key, creator_id uuid not null, target_user_id uuid, time_control text not null,
  rules_id text not null, status text not null, match_id uuid, accepted_by uuid,
  created_at timestamptz not null default now(), expires_at timestamptz not null);
create table bot_keys (key_id text primary key, bot_user_id uuid not null references players(id), owner_user_id uuid not null,
  bot_name text not null, secret_hash bytea not null, created_at timestamptz not null default now(), revoked_at timestamptz);
create table reward_grants (nonce text primary key, user_id uuid not null, kind text not null, game_id uuid,
  granted_at timestamptz not null default now());
create table seasons (id serial primary key, starts_at timestamptz not null, ends_at timestamptz not null);
create table master_season_results (season_id int not null references seasons(id), user_id uuid not null,
  final_rating double precision not null, rank int not null, primary key (season_id, user_id));
