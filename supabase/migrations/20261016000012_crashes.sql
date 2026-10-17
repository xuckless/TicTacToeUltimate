-- 0012: crash and bug reports. Crashes with the same fingerprint (exception type + top app frames) share a group; the admin
-- site files a GitHub issue per group (automatically once 5+ players hit it within 24 h). Bug screenshots live in the private
-- storage bucket bug-screenshots and are deleted after 90 days by the server.
create table crash_groups (
  fingerprint text primary key,
  kind text not null check (kind in ('crash','bug')),
  title text not null,
  first_seen timestamptz not null,
  last_seen timestamptz not null,
  count int not null default 0,
  status text not null default 'open' check (status in ('open','ignored','fixed')),
  gh_issue_url text,
  platforms text[] not null default '{}'
);
create index crash_groups_seen on crash_groups (last_seen desc);

create table crash_reports (
  id bigserial primary key,
  fingerprint text not null references crash_groups(fingerprint) on delete cascade,
  kind text not null,
  user_id uuid,
  username text,
  platform text not null,
  app_version text not null,
  os text, device text, browser text, locale text, screen text,
  route text, component text,
  exception_type text, message text, stack text,
  inner_exceptions jsonb not null default '[]'::jsonb,
  logs jsonb not null default '[]'::jsonb,
  trail jsonb not null default '[]'::jsonb,
  description text,
  screenshot_path text,
  at timestamptz not null
);
create index crash_reports_group on crash_reports (fingerprint, at desc);
create index crash_reports_user on crash_reports (user_id, at desc);

alter table crash_groups enable row level security;
alter table crash_reports enable row level security;

insert into storage.buckets (id, name, public) values ('bug-screenshots', 'bug-screenshots', false) on conflict (id) do nothing;
