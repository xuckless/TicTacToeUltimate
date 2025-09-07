CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS tttu AUTHORIZATION tttu;
ALTER DATABASE tictactoe_cold SET search_path = tttu, public;

CREATE TABLE IF NOT EXISTS players (
    player_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sub UUID NOT NULL, -- from provider (Google/Apple)
    username VARCHAR(32) UNIQUE NOT NULL,
    elo INT DEFAULT 500,
    email TEXT UNIQUE NOT NULL,
    age INT
);