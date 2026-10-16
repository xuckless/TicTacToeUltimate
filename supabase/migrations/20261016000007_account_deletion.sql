-- 0007: in-app account deletion keeps an anonymised player row ("deleted") so game foreign keys hold.
alter table players drop constraint players_kind_check;
alter table players add constraint players_kind_check check (kind in ('guest','account','bot','deleted'));
