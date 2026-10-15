-- 0003: friends, blocks, direct messages (§9)
create table friend_requests (id uuid primary key, from_id uuid not null, to_id uuid not null,
  created_at timestamptz not null default now(), unique (from_id, to_id));
create table friendships (user_lo uuid not null, user_hi uuid not null, created_at timestamptz not null default now(),
  primary key (user_lo, user_hi), check (user_lo < user_hi));
create table blocks (blocker uuid not null, blocked uuid not null, created_at timestamptz not null default now(),
  primary key (blocker, blocked));
create table direct_messages (id bigserial primary key, sender uuid not null, recipient uuid not null,
  body text not null check (char_length(body) between 1 and 500), sent_at timestamptz not null default now(), read_at timestamptz);
create index dm_thread on direct_messages (least(sender, recipient), greatest(sender, recipient), id desc);
