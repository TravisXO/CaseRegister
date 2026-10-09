-- Least-privilege runtime role for CaseRegister.
--
-- Run this as the database owner (the role that runs migrations, neondb_owner on Neon) against the
-- target database, after the migrations have been applied. It is idempotent, so it can be re-run
-- on a new or rebuilt database. It contains no passwords.
--
-- Prerequisite: the login role exists. Create it with SQL as the owner, using a long random password:
--   CREATE ROLE case_register_app LOGIN PASSWORD '<generate a random password>';
-- Do NOT create it with the Neon CLI, console or API: those roles join neon_superuser (CREATEDB,
-- CREATEROLE, BYPASSRLS, broad data access) and the database owner cannot revoke that membership.
-- Store the connection string in user-secrets (ConnectionStrings:Default) or in the
-- ConnectionStrings__Default environment variable.
--
-- The application connects as case_register_app and never as the owner. It gets data access only:
-- no DDL, no role or database creation, no row-level-security bypass.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'case_register_app') THEN
        RAISE EXCEPTION 'Role case_register_app does not exist. Create it first with CREATE ROLE ... LOGIN PASSWORD (see the header of this file).';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_auth_members m
        JOIN pg_roles granted ON granted.oid = m.roleid
        JOIN pg_roles member ON member.oid = m.member
        WHERE granted.rolname = 'neon_superuser' AND member.rolname = 'case_register_app') THEN
        RAISE EXCEPTION 'Role case_register_app is a member of neon_superuser, which is too privileged and cannot be revoked by the owner. Drop it and recreate it with CREATE ROLE ... LOGIN PASSWORD (see the header of this file).';
    END IF;
END
$$;

-- Make sure the role carries no elevated attributes, whatever way it was created.
ALTER ROLE case_register_app NOCREATEDB NOCREATEROLE NOBYPASSRLS LOGIN;

-- Connect, and use (but not create in) the public schema.
DO $$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO case_register_app', current_database());
END
$$;

GRANT USAGE ON SCHEMA public TO case_register_app;
REVOKE CREATE ON SCHEMA public FROM case_register_app;

-- Row-level data access on existing tables.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO case_register_app;

-- The audit log is append-only: the application can add rows and read them, never change or remove one.
DO $$
BEGIN
    IF to_regclass('public.audit_log') IS NOT NULL THEN
        REVOKE UPDATE, DELETE, TRUNCATE ON TABLE public.audit_log FROM case_register_app;
    END IF;
END
$$;

-- Nothing is hard-deleted. Accounts, authenticators, recovery codes and lockout history are retired or
-- marked, never removed, so the application role may not delete from them. (Sessions are the exception:
-- ending one means deleting its row, on purpose.)
DO $$
DECLARE
    protected_table text;
BEGIN
    FOREACH protected_table IN ARRAY ARRAY['users', 'user_authenticators', 'recovery_codes', 'lockout_events']
    LOOP
        IF to_regclass(format('public.%I', protected_table)) IS NOT NULL THEN
            EXECUTE format('REVOKE DELETE, TRUNCATE ON TABLE public.%I FROM case_register_app', protected_table);
        END IF;
    END LOOP;
END
$$;

-- The runtime role has no business with the migration history: only the owner applies migrations.
DO $$
BEGIN
    IF to_regclass('public.__ef_migrations_history') IS NOT NULL THEN
        REVOKE ALL ON TABLE public.__ef_migrations_history FROM case_register_app;
    END IF;
END
$$;

-- The same grants for tables the owner creates in future migrations.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO case_register_app;
