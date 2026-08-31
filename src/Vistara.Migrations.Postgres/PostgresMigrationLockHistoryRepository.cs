using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

#pragma warning disable EF1001 // The migration lock is a provider concern, so the provider history repository is the only valid base.
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

namespace Vistara.Migrations.Postgres;

/// <summary>
/// Serializes concurrently started migration bundles with a session-scoped
/// PostgreSQL advisory lock.
/// </summary>
/// <remarks>
/// The provider default locks the history table inside each migration
/// transaction, so the lock is dropped every time a migration commits. A bundle
/// that loses the race then resumes the migration list it computed before the
/// winner applied anything, replaying data definition statements over objects
/// that already exist. An advisory lock is not tied to a transaction or to any
/// table, so it also covers creating the history table on a fresh database, and
/// it is held until the migration run releases it or the connection closes.
/// </remarks>
public sealed class PostgresMigrationLockHistoryRepository : NpgsqlHistoryRepository
{
    /// <summary>
    /// Identifies the Vistara schema migration lock inside PostgreSQL's single
    /// advisory lock space. It is an arbitrary constant that must never change.
    /// </summary>
    public const long AdvisoryLockKey = 4_920_170_632_517_051_121L;

    private const string LockTimeoutVariable = "VISTARA_MIGRATION_LOCK_TIMEOUT_SECONDS";

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="PostgresMigrationLockHistoryRepository"/> class.
    /// </summary>
    /// <param name="dependencies">The history repository dependencies.</param>
    public PostgresMigrationLockHistoryRepository(HistoryRepositoryDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// Gets the release behaviour of the migration lock. A session advisory
    /// lock outlives every migration transaction and is released when the
    /// migration connection closes.
    /// </summary>
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    /// <inheritdoc />
    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();

        var command = Dependencies.RawSqlCommandBuilder.Build(
            FormattableString.Invariant($"SELECT pg_try_advisory_lock({AdvisoryLockKey});"));

        var timeout = ResolveLockTimeout();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (Equals(command.ExecuteScalar(CreateLockCommandParameters()), true))
            {
                return new PostgresMigrationDatabaseLock(this);
            }

            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException(DescribeTimeout(timeout));
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <inheritdoc />
    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();

        var command = Dependencies.RawSqlCommandBuilder.Build(
            FormattableString.Invariant($"SELECT pg_try_advisory_lock({AdvisoryLockKey});"));

        var timeout = ResolveLockTimeout();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var acquired = await command
                .ExecuteScalarAsync(CreateLockCommandParameters(), cancellationToken)
                .ConfigureAwait(false);
            if (Equals(acquired, true))
            {
                return new PostgresMigrationDatabaseLock(this);
            }

            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException(DescribeTimeout(timeout));
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan ResolveLockTimeout()
    {
        var configured = Environment.GetEnvironmentVariable(LockTimeoutVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultLockTimeout;
        }

        if (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"{LockTimeoutVariable} must be a positive number of seconds, but was '{configured}'."));
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string DescribeTimeout(TimeSpan timeout)
        => FormattableString.Invariant(
            $"Another migration run held the Vistara migration lock for longer than {timeout.TotalSeconds} seconds. Set {LockTimeoutVariable} to allow a longer wait.");

    private RelationalCommandParameterObject CreateLockCommandParameters()
        => new(
            Dependencies.Connection,
            parameterValues: null,
            readerColumns: null,
            Dependencies.CurrentContext.Context,
            Dependencies.CommandLogger,
            CommandSource.Migrations);

    private sealed class PostgresMigrationDatabaseLock(PostgresMigrationLockHistoryRepository repository)
        : IMigrationsDatabaseLock
    {
        private bool _released;

        IHistoryRepository IMigrationsDatabaseLock.HistoryRepository => repository;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            repository.Dependencies.RawSqlCommandBuilder
                .Build(FormattableString.Invariant($"SELECT pg_advisory_unlock({AdvisoryLockKey});"))
                .ExecuteScalar(repository.CreateLockCommandParameters());
        }

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await repository.Dependencies.RawSqlCommandBuilder
                .Build(FormattableString.Invariant($"SELECT pg_advisory_unlock({AdvisoryLockKey});"))
                .ExecuteScalarAsync(repository.CreateLockCommandParameters())
                .ConfigureAwait(false);
        }
    }
}
#pragma warning restore EF1001
