using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Vistara.E2E.HostTests;

public sealed class E2eSeedTests
{
    [Fact]
    public async Task Seed_creates_the_canonical_schema_once()
    {
        string repositoryRoot = FindRepositoryRoot();
        string testRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".artifacts",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(testRoot, "vistara-e2e.db");
        string statePath = Path.Combine(testRoot, "state.json");

        try
        {
            Assembly host = Assembly.Load("Vistara.E2E.Host");
            MethodInfo entryPoint = host.EntryPoint
                ?? throw new InvalidOperationException(
                    "The E2E host has no entry point.");
            object? seed = entryPoint.Invoke(
                null,
                [
                    new[]
                    {
                        "seed",
                        "--database",
                        databasePath,
                        "--media-root",
                        Path.Combine(testRoot, "media"),
                        "--fixture",
                        Path.Combine(
                            repositoryRoot,
                            "tests",
                            "Vistara.E2E",
                            "fixtures",
                            "tiny.png.base64"),
                        "--state",
                        statePath,
                        "--pepper",
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                    },
                ]);

            if (seed is Task seedTask)
            {
                await seedTask;
            }

            Assert.True(File.Exists(statePath));
            await using var connection =
                new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                      'sharing_idempotency',
                      'sharing_rate_limits',
                      'sharing_sessions',
                      'sharing_shares');
                """;
            Assert.Equal(4L, await command.ExecuteScalarAsync());
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vistara.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
