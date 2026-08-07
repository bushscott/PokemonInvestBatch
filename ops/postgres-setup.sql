-- PokemonInvestBatch — one-time Postgres setup on the Pi.
-- Run as the postgres superuser:  sudo -u postgres psql -f postgres-setup.sql
--
-- CHANGE THE THREE PASSWORDS BELOW BEFORE RUNNING.
-- They then go into (gitignored) appsettings.Production.json / user secrets.

-- Owner role: runs migrations, holds DDL. The app NEVER connects as this.
CREATE ROLE pokemon_owner LOGIN PASSWORD 'CHANGE_ME_OWNER';

-- App role: least privilege. SELECT/INSERT only, plus UPDATE on the two
-- tables that carry mutable state. No DELETE anywhere — the store is
-- append-only by design, and the role enforces it.
CREATE ROLE pokemon_app LOGIN PASSWORD 'CHANGE_ME_APP';

-- Test role: integration tests only, and no rights at all on the real
-- database. CREATEDB because each test builds its own throwaway database and
-- drops it again — a shared test database means one suite can truncate
-- another's fixtures mid-assertion, and the failure looks like a bug in the
-- code under test rather than in the harness.
CREATE ROLE pokemon_tester LOGIN PASSWORD 'CHANGE_ME_TEST' CREATEDB;

CREATE DATABASE pokemon OWNER pokemon_owner;

-- Only a template: the tests read its connection string for host and
-- credentials, then create and drop databases of their own beside it.
CREATE DATABASE pokemon_test OWNER pokemon_tester;

\connect pokemon

-- Grants apply to tables the owner has created *and* will create in future
-- migrations, so re-running this file after new migrations is unnecessary.
GRANT USAGE ON SCHEMA public TO pokemon_app;

ALTER DEFAULT PRIVILEGES FOR ROLE pokemon_owner IN SCHEMA public
    GRANT SELECT, INSERT ON TABLES TO pokemon_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pokemon_owner IN SCHEMA public
    GRANT USAGE ON SEQUENCES TO pokemon_app;

-- Mutable exceptions, applied after the first migration has created them:
--   cards  — scheduler state (last_visited_at, churn, cap flag), image hash
--   shapes — last_seen_at on re-observed fingerprints
--   sets   — last_seen_at on re-discovery
-- Run AFTER `dotnet ef database update`:
--   GRANT UPDATE ON cards, shapes, sets TO pokemon_app;
