-- 0009: device push tokens (APNs now, FCM later). One row per device; a token moves with whoever signs in on the device.
create table push_tokens (token text primary key, user_id uuid not null, platform text not null check (platform in ('ios','android')),
  updated_at timestamptz not null default now());
create index push_tokens_user on push_tokens (user_id);
