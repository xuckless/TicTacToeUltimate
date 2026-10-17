-- 0010: moderation. Player reports, penalties (warn / chat mute / ban / username reset / shadow pool), auto-moderation
-- strikes, appeals, device bans, the admin audit log, and the player's language (localized pushes).
-- The server uses the service role; RLS is on with no policies (as in 0005).

alter table players add column if not exists locale text;
alter table players add column if not exists shadow_pool boolean not null default false;

create table reports (
  id bigserial primary key,
  reporter uuid not null,
  target uuid not null,
  kind text not null check (kind in ('chat','profile','cheating')),
  reason text not null,
  note text,
  message_id bigint,
  game_id uuid,
  evidence jsonb not null default '{}'::jsonb,
  status text not null default 'open' check (status in ('open','actioned','dismissed')),
  ai_verdict jsonb,
  created_at timestamptz not null default now(),
  resolved_at timestamptz,
  resolution text
);
create index reports_open on reports (status, created_at desc);
create index reports_target on reports (target, created_at desc);
-- One open report per reporter/target/kind/message: repeats fold into the existing one.
create unique index reports_dedupe on reports (reporter, target, kind, coalesce(message_id, 0)) where status = 'open';

create table penalties (
  id bigserial primary key,
  user_id uuid not null,
  kind text not null check (kind in ('warn','chat_mute','ban','username_reset','shadow')),
  reason text not null,
  starts_at timestamptz not null default now(),
  ends_at timestamptz,                 -- null = permanent (bans, shadow) or instant (warn, username_reset)
  source text not null check (source in ('auto','admin')),
  report_id bigint references reports(id) on delete set null,
  created_by text,                     -- admin email, or 'auto'
  revoked_at timestamptz,
  revoked_by text
);
create index penalties_user on penalties (user_id, starts_at desc);

create table moderation_events (
  id bigserial primary key,
  user_id uuid not null,
  kind text not null check (kind in ('filter_block','ai_block','ai_hide','username_block','report_confirmed')),
  text text,
  detail jsonb not null default '{}'::jsonb,
  at timestamptz not null default now()
);
create index moderation_events_user on moderation_events (user_id, at desc);
create index moderation_events_at on moderation_events (at desc);

create table appeals (
  id bigserial primary key,
  penalty_id bigint not null unique references penalties(id) on delete cascade,
  user_id uuid not null,
  text text not null check (char_length(text) between 1 and 1000),
  status text not null default 'open' check (status in ('open','approved','denied')),
  created_at timestamptz not null default now(),
  decided_at timestamptz,
  decision_note text
);
create index appeals_open on appeals (status, created_at);

create table player_devices (
  user_id uuid not null,
  device_hash text not null,
  first_seen timestamptz not null default now(),
  last_seen timestamptz not null default now(),
  primary key (user_id, device_hash)
);
create index player_devices_hash on player_devices (device_hash);

create table device_bans (
  device_hash text not null,
  penalty_id bigint not null references penalties(id) on delete cascade,
  primary key (device_hash, penalty_id)
);

create table admin_audit (
  id bigserial primary key,
  admin_email text not null,
  action text not null,
  target text,
  detail jsonb not null default '{}'::jsonb,
  at timestamptz not null default now()
);
create index admin_audit_at on admin_audit (at desc);

-- Direct messages hidden by moderation stay as evidence but are no longer shown.
alter table direct_messages add column if not exists hidden_at timestamptz;

alter table reports enable row level security;
alter table penalties enable row level security;
alter table moderation_events enable row level security;
alter table appeals enable row level security;
alter table player_devices enable row level security;
alter table device_bans enable row level security;
alter table admin_audit enable row level security;
-- 0008/0009 tables were created without RLS: close that gap too.
alter table wallets enable row level security;
alter table diamond_ledger enable row level security;
alter table inventory enable row level security;
alter table push_tokens enable row level security;
