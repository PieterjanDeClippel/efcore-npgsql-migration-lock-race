using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

namespace Repro;

/// <summary>
///     Workaround for Npgsql.EntityFrameworkCore.PostgreSQL 11.0.0: holds the migration lock for the whole
///     <c>Migrate()</c> run with a session-level advisory lock, the way SQL Server's <c>sp_getapplock</c> does.
/// </summary>
/// <remarks>
///     The stock repository takes <c>LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE</c> with
///     <see cref="LockReleaseBehavior.Transaction" />. EF's migrator plans the pending migrations once, under the first
///     lock, then runs each migration in its own transaction and reacquires the lock per transaction. Between one
///     migration's commit and the next lock, another instance takes the lock, reads the history and applies the next
///     migration; the first instance then applies it again from its stale plan. With
///     <see cref="LockReleaseBehavior.Connection" /> the migrator takes the lock once, before it reads the history, and
///     keeps it until the run ends.
///     Register with <c>options.ReplaceService&lt;IHistoryRepository, AdvisoryLockHistoryRepository&gt;()</c>.
/// </remarks>
#pragma warning disable EF1001 // NpgsqlHistoryRepository is an internal API.
public sealed class AdvisoryLockHistoryRepository(HistoryRepositoryDependencies dependencies)
    : NpgsqlHistoryRepository(dependencies)
#pragma warning restore EF1001
{
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        Command($"SELECT pg_advisory_lock({LockKey})").ExecuteNonQuery(Parameters());
        return new AdvisoryLock(this);
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        await Command($"SELECT pg_advisory_lock({LockKey})").ExecuteNonQueryAsync(Parameters(), cancellationToken)
            .ConfigureAwait(false);
        return new AdvisoryLock(this);
    }

    // Advisory locks are per database, so one key per history table is enough. A number computed here, spliced as a
    // numeric literal: no text from configuration reaches the SQL.
    private long LockKey
    {
        get
        {
            var name = $"efcore:migrations:{TableSchema}.{TableName}";
            var hash = 14695981039346656037UL; // FNV-1a, 64 bit
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                hash = (hash ^ b) * 1099511628211UL;
            }

            return unchecked((long)hash);
        }
    }

    private IRelationalCommand Command(string sql) => Dependencies.RawSqlCommandBuilder.Build(sql);

    private RelationalCommandParameterObject Parameters()
        => new(
            Dependencies.Connection,
            null,
            null,
            Dependencies.CurrentContext.Context,
            Dependencies.CommandLogger,
            CommandSource.Migrations);

    private sealed class AdvisoryLock(AdvisoryLockHistoryRepository repository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository => repository;

        // A session lock ends with the session anyway, so there is nothing to release once the connection is closed.
        public void Dispose()
        {
            if (repository.Dependencies.Connection.DbConnection.State == ConnectionState.Open)
            {
                repository.Command($"SELECT pg_advisory_unlock({repository.LockKey})").ExecuteNonQuery(repository.Parameters());
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (repository.Dependencies.Connection.DbConnection.State == ConnectionState.Open)
            {
                await repository.Command($"SELECT pg_advisory_unlock({repository.LockKey})")
                    .ExecuteNonQueryAsync(repository.Parameters()).ConfigureAwait(false);
            }
        }
    }
}
