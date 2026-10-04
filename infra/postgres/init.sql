-- ===========================================================================
-- SPA «Комментарии» — PostgreSQL cluster tuning (runs ONCE on empty data dir)
--
-- IMPORTANT: the application schema is owned exclusively by EF Core migrations
-- (src/Backend/Comments.Infrastructure/Persistence/Migrations). This script must
-- never create or alter application tables; it only tunes the cluster and
-- enables extensions the search/cache paths may exploit.
-- ===========================================================================

-- All timestamps in the domain are UTC (API-v2 §8: `timestamptz`).
ALTER DATABASE comments SET timezone TO 'UTC';

-- Trigram matching: optional accelerator for ILIKE / fuzzy userName-email
-- lookups. Idempotent; harmless if never used by the app.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Keep the default text-search config stable across locales.
ALTER DATABASE comments SET default_text_search_config TO 'pg_catalog.english';
