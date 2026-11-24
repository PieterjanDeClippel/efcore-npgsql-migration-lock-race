# Concurrent `Migrate()` applies the same migration twice on PostgreSQL (EF Core 11 + Npgsql 11)

Since EF Core 9, `Database.Migrate()` takes a database lock so that several instances of an app can start together.
On PostgreSQL the lock does not cover the whole run, and instances re-apply migrations that another instance has
already applied.

```
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:18
dotnet run            # or set REPRO_PG to another server's connection string
```

The program recreates an empty database, then starts three `MigrateAsync()` calls together against it. There are
three migrations; the second one sleeps three seconds, like a data migration or an index build. Typical output (3 of
3 runs on Npgsql.EntityFrameworkCore.PostgreSQL 11.0.0, PostgreSQL 18):

```
OK   instance 1
FAIL instance 2: PostgresException: 42701: column "Email" of relation "People" already exists
FAIL instance 3: PostgresException: 42701: column "Phone" of relation "People" already exists
history rows: 3 (3 expected)
```

The same three instances against SQL Server (`sp_getapplock`, `LockReleaseBehavior.Connection`) apply each
migration once.

## Cause

- `NpgsqlHistoryRepository` takes `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE` and declares
  `LockReleaseBehavior.Transaction`, so the lock ends with each migration's transaction.
- EF's `Migrator.MigrateImplementationAsync` (`Migrator.cs:295-311`, release/11.0) reads the applied migrations
  **once**, under the first lock, and builds the whole plan from it.
- `MigrationCommandExecutor` (`MigrationCommandExecutor.cs:225-236`) commits each migration and, for the next one,
  begins a new transaction and reacquires the lock (`ReacquireIfNeededAsync`, `transactionRestarted: true`). The plan
  is not re-read after reacquiring.
- Between instance A's commit of migration 1 and its next lock, instance B, blocked on `LOCK TABLE`, gets the lock,
  reads the history (migration 1 applied) and applies migration 2. A then gets the lock back and applies migration 2
  again from its stale plan.

So the provider's lock is correct per transaction, but EF's migrator never re-checks the history after a
transaction-scoped lock is reacquired. Npgsql is the provider that uses `LockReleaseBehavior.Transaction`.

## Fix (this branch)

**Workaround, Npgsql 11.0.0 as released:**
[`AdvisoryLockHistoryRepository`](AdvisoryLockHistoryRepository.cs), registered with
`options.ReplaceService<IHistoryRepository, AdvisoryLockHistoryRepository>()`. It takes a session-level
`pg_advisory_lock` and declares `LockReleaseBehavior.Connection`, so the migrator takes the lock once, before it
reads the history, and holds it until the run ends, as on SQL Server. With it, `dotnet run` passes (3 of 3 runs):

```
OK   instance 1
OK   instance 2
OK   instance 3
history rows: 3 (3 expected)
```

**Proposed Npgsql change:** [`upstream/efcore.pg-hotfix-11.0.1.patch`](upstream/efcore.pg-hotfix-11.0.1.patch),
against `hotfix/11.0.1` (applies cleanly with `git apply`): the same change in `NpgsqlHistoryRepository` itself.
The lock key is a number computed from the history table's schema-qualified name, so no text is spliced into the
SQL. The patch has not been built or run in Npgsql's own test suite; the workaround above is the same code,
outside the provider.

**Trade-off:** a session-level advisory lock needs a session that lasts the whole run, so it does not hold through
PgBouncer in transaction pooling mode, where `LOCK TABLE` inside a transaction does. Running migrations through a
transaction-mode pooler is already unsafe for other reasons, and the same is true of SQL Server's session lock.

**The other place to fix it** is EF Core's migrator: after reacquiring a `LockReleaseBehavior.Transaction` lock
for the next migration, re-read the history and skip migrations another instance applied meanwhile. That would
protect any provider using transaction-scoped locks, but it changes `Migrator` and `MigrationCommandExecutor`, so
it is described here rather than patched.
