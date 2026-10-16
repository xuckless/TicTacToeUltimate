-- 0008: Diamonds economy. Append-only ledger + a wallet row kept in step; inventory for timed passes.
create table wallets (user_id uuid primary key references players(id) on delete cascade, balance int not null default 0 check (balance >= 0),
  updated_at timestamptz not null default now());
create table diamond_ledger (id bigserial primary key, user_id uuid not null, delta int not null, reason text not null, ref text not null,
  created_at timestamptz not null default now(), unique (user_id, reason, ref));
create index diamond_ledger_user_time on diamond_ledger (user_id, created_at desc);
create table inventory (user_id uuid not null, item_id text not null, acquired_at timestamptz not null default now(), expires_at timestamptz,
  primary key (user_id, item_id, acquired_at));
