-- Grants for the CI role nivora_ci inside nivora_test.
--
-- Run by the owner by hand, as the shared role, connected to nivora_test,
-- after EVERY rebuild: a rebuild drops nivora_test and with it every grant
-- below. See README.md, "CI role".
--
-- No password is in this file and none is needed: the role already exists.
-- The role gets what the test suite does and nothing else: read and write
-- rows. No DDL, no TRUNCATE, no CREATE on the schema, and read-only on the
-- migration history (CI never migrates).

BEGIN;

DO $$
BEGIN
    IF current_database() <> 'nivora_test' THEN
        RAISE EXCEPTION 'grant refused: wrong database';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nivora_ci') THEN
        RAISE EXCEPTION 'grant refused: the role nivora_ci does not exist';
    END IF;
END
$$;

GRANT CONNECT ON DATABASE nivora_test TO nivora_ci;
GRANT USAGE ON SCHEMA public TO nivora_ci;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO nivora_ci;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO nivora_ci;
REVOKE INSERT, UPDATE, DELETE ON TABLE "__EFMigrationsHistory" FROM nivora_ci;

COMMIT;

-- One row: tables is the number of tables in public, readable is how many of
-- them nivora_ci can SELECT, history_writable must be f. tables = readable.
SELECT count(*) AS tables,
       count(*) FILTER (WHERE has_table_privilege('nivora_ci', c.oid, 'SELECT')) AS readable,
       has_table_privilege('nivora_ci', '"__EFMigrationsHistory"', 'INSERT') AS history_writable
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'r';
