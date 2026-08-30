using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Domain.Jobs;
using Vistara.Persistence;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;

namespace Vistara.PerformanceTests;

internal sealed record SqliteContentionResult(
    int BusyErrors,
    int OperationErrors,
    string Detail);

internal static class SqliteContentionScenario
{
    private const int UploadClients = 8;
    private const int UploadsPerClient = 8;
    private const int Jobs = 32;
    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<SqliteContentionResult> RunAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        string databasePath = Path.Combine(paths.ArtifactsDirectory, "sqlite-contention.db");
        DeleteDatabase(databasePath);
        try
        {
            string connectionString =
                $"Data Source={databasePath};Default Timeout=5;Pooling=False";
            var vistaraOptions = new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var jobOptions = new DbContextOptionsBuilder<JobDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using (var context = new VistaraDbContext(
                             vistaraOptions,
                             new FixedTenantScope(TestIds.Tenant)))
            {
                await context.Database.EnsureCreatedAsync(cancellationToken);
                await context.Database.ExecuteSqlRawAsync(
                    "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;",
                    cancellationToken);
                await SeedTenantAsync(context, cancellationToken);
            }

            int busyErrors = 0;
            int operationErrors = 0;
            var errorDetails = new ConcurrentQueue<string>();
            void RecordBusy(Exception exception)
            {
                Interlocked.Increment(ref busyErrors);
                errorDetails.Enqueue(ExceptionSummary.Create("SQLite busy", exception));
            }

            void RecordError(Exception exception)
            {
                Interlocked.Increment(ref operationErrors);
                errorDetails.Enqueue(ExceptionSummary.Create(
                    "Contention operation failed",
                    exception));
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] uploadTasks = Enumerable.Range(0, UploadClients)
                .Select(client => RunUploadClientAsync(
                    client,
                    start.Task,
                    vistaraOptions,
                    RecordBusy,
                    RecordError,
                    cancellationToken))
                .ToArray();
            Task jobTask = RunJobWorkerAsync(
                start.Task,
                jobOptions,
                RecordBusy,
                RecordError,
                cancellationToken);
            start.SetResult();
            await Task.WhenAll(uploadTasks.Append(jobTask));

            await using (var context = new VistaraDbContext(
                             vistaraOptions,
                             new FixedTenantScope(TestIds.Tenant)))
            {
                int uploadCount =
                    await context.UploadSessions.CountAsync(cancellationToken);
                int jobCount = await context.Jobs.CountAsync(cancellationToken);
                if (uploadCount != UploadClients * UploadsPerClient ||
                    jobCount != Jobs)
                {
                    RecordError(new InvalidOperationException(
                        $"Persisted {uploadCount}/{UploadClients * UploadsPerClient} " +
                        $"uploads and {jobCount}/{Jobs} jobs."));
                }
            }

            string detail = busyErrors == 0 && operationErrors == 0
                ? $"Persisted {UploadClients * UploadsPerClient} upload sessions " +
                  $"and {Jobs} jobs without SQLITE_BUSY or operation failures."
                : string.Join(" | ", errorDetails.Take(3));
            return new SqliteContentionResult(
                busyErrors,
                operationErrors,
                detail);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(databasePath);
        }
    }

    private static async Task RunUploadClientAsync(
        int client,
        Task start,
        DbContextOptions<VistaraDbContext> options,
        Action<Exception> onBusy,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        await start;
        for (int index = 0; index < UploadsPerClient; index++)
        {
            try
            {
                await using var context = new VistaraDbContext(
                    options,
                    new FixedTenantScope(TestIds.Tenant));
                long sequence = 500_000 + client * 1_000L + index;
                context.UploadSessions.Add(new UploadSessionRow
                {
                    Id = TestIds.Create(sequence),
                    TenantId = TestIds.Tenant,
                    ActorId = TestIds.Actor,
                    DisplayFileName = $"client-{client}-upload-{index}.jpg",
                    Strategy = "Proxy",
                    StagingKey = $"staging/performance/{client}/{index}",
                    StorageProvider = "local",
                    StorageContainer = "performance",
                    ExpectedBytes = 1024,
                    ExpectedSha256 = new string('b', 64),
                    DeclaredContentType = "image/jpeg",
                    State = "Pending",
                    ExpiresAtUtc = UtcNow.AddHours(1),
                    CreatedAtUtc = UtcNow,
                    UpdatedAtUtc = UtcNow,
                    Version = 1,
                });
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (IsBusy(exception))
            {
                onBusy(exception);
            }
            catch (Exception exception)
            {
                onError(exception);
            }
        }
    }

    private static async Task RunJobWorkerAsync(
        Task start,
        DbContextOptions<JobDbContext> options,
        Action<Exception> onBusy,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        await start;
        for (int index = 0; index < Jobs; index++)
        {
            try
            {
                await using var context = new JobDbContext(
                    options,
                    new FixedTenantScope(TestIds.Tenant));
                var queue = new RelationalJobQueue(
                    context,
                    new JobQueueOptions { ConfiguredWorkerCount = 1 });
                DurableJob job = DurableJob.Create(
                    new JobId(TestIds.Create(700_000 + index)),
                    new JobTenantId(TestIds.Tenant),
                    new JobType("performance.job"),
                    """{"safe":true}""",
                    1,
                    new JobDedupeKey($"performance-{index}"),
                    10,
                    3,
                    UtcNow,
                    UtcNow,
                    "00-performance");
                if ((await queue.EnqueueAsync(job, cancellationToken)).IsFailure)
                {
                    onError(new InvalidOperationException(
                        $"Enqueuing performance job {index} failed."));
                }
            }
            catch (Exception exception) when (IsBusy(exception))
            {
                onBusy(exception);
            }
            catch (Exception exception)
            {
                onError(exception);
            }
        }

        try
        {
            await using var context = new JobDbContext(
                options,
                new FixedTenantScope(TestIds.Tenant));
            var queue = new RelationalJobQueue(
                context,
                new JobQueueOptions { ConfiguredWorkerCount = 1 });
            var request = new Vistara.Application.Jobs.JobLeaseRequest(
                new JobLeaseOwner("performance-worker"),
                UtcNow,
                TimeSpan.FromMinutes(1),
                Jobs);
            if ((await queue.LeaseAsync(request, cancellationToken)).IsFailure)
            {
                onError(new InvalidOperationException(
                    "Leasing the seeded performance jobs failed."));
            }
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            onBusy(exception);
        }
        catch (Exception exception)
        {
            onError(exception);
        }
    }

    private static async Task SeedTenantAsync(
        VistaraDbContext context,
        CancellationToken cancellationToken)
    {
        context.Tenants.Add(new TenantRow
        {
            Id = TestIds.Tenant,
            TenantId = TestIds.Tenant,
            Slug = "performance-contention",
            Name = "Performance contention",
            Status = "Active",
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = TestIds.Actor,
            NormalizedEmail = "contention@example.test",
            DisplayName = "Contention",
            Status = "Active",
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            Version = 1,
        });
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = TestIds.Tenant,
            UserId = TestIds.Actor,
            Role = "Member",
            Status = "Active",
            InvitedAtUtc = UtcNow,
            JoinedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            Version = 1,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsBusy(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                sqlite.SqliteErrorCode is 5 or 6)
            {
                return true;
            }
        }

        return false;
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
