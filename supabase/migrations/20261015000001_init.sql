-- 0001: players + ratings (§9)
create extension if not exists citext;

create table players (
  id uuid primary key,                                   -- = JWT sub (Supabase auth uid, dev id, or bot id); NO fk to auth.users
  kind text not null check (kind in ('guest','account','bot')),
  username citext unique check (username is null or (username::text ~ '^[a-z0-9_]{3,16}$' and username::text = lower(username::text))),
  display_name text not null,
  avatar_id text not null default 'a1',
  country char(2),
  time_zone text not null default 'UTC',
  renames_left smallint not null default 1,
  tos_accepted_at timestamptz,
  device_id text unique,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now());

create table ratings (
  user_id uuid not null references players(id) on delete cascade,
  pool text not null check (pool in ('Ranked','CasualX2','CasualX4','Unlimited','Daily')),
  rating double precision not null default 1200, rd double precision not null default 350,
  volatility double precision not null default 0.06, games int not null default 0, wins int not null default 0,
  losses int not null default 0, draws int not null default 0, peak double precision not null default 1200,
  last_game_at timestamptz, primary key (user_id, pool));
create index ratings_board on ratings (pool, rating desc);
