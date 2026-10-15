-- Arena (health) mode: own rating pools (Arena Ranked Ladder visible, Casual Arena hidden).
alter table ratings drop constraint if exists ratings_pool_check;
alter table ratings add constraint ratings_pool_check
  check (pool in ('Ranked','CasualX2','CasualX4','Unlimited','Daily','ArenaCasual','ArenaRanked'));
