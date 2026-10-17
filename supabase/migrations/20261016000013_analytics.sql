-- 0013: first-party analytics (owner decision 2026-10-16: our own database, no third-party tracker).
-- Raw events are kept 14 days (free-plan size limit); daily_stats rollups are kept forever.
create table events (
  id bigserial primary key,
  at timestamptz not null,
  user_id uuid,
  device_hash text,
  session_id text,
  platform text,
  app_version text,
  locale text,
  country text,
  campaign text,
  kind text not null,
  name text not null,
  props jsonb not null default '{}'::jsonb
);
create index events_at on events (at);
create index events_kind_at on events (kind, at);

create table daily_stats (
  day date not null,
  metric text not null,
  dim text not null default '',
  value bigint not null,
  primary key (day, metric, dim)
);

create table campaigns (
  slug text primary key check (slug ~ '^[a-z0-9-]{2,40}$'),
  name text not null,
  channel text not null,
  ios_url text,
  android_url text,
  web_url text,
  created_at timestamptz not null default now(),
  archived boolean not null default false
);

create table campaign_clicks (
  id bigserial primary key,
  slug text not null references campaigns(slug) on delete cascade,
  at timestamptz not null,
  platform text not null,
  country text
);
create index campaign_clicks_slug on campaign_clicks (slug, at);

alter table players add column if not exists campaign text;
create index if not exists games_ended_at on games (ended_at);
create index if not exists players_created_at on players (created_at);

alter table events enable row level security;
alter table daily_stats enable row level security;
alter table campaigns enable row level security;
alter table campaign_clicks enable row level security;
