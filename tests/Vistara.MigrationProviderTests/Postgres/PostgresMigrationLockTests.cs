using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Migrations.Postgres;
using Vistara.Persistence;
using Xunit;

namespace Vistara.MigrationProviderTests.Postgres;

public sealed class PostgresMigrationLockTests
{
    [Fact]
    public void Design_time_context_holds_the_migration_lock_beyond_each_transaction()
    {
        using VistaraDbContext context = new VistaraDbContextFactory().CreateDbContext([]);

        IHistoryRepository historyRepository = context.GetService<IHistoryRepository>();

        Assert.IsType<PostgresMigrationLockHistoryRepository>(historyRepository);
        Assert.Equal(LockReleaseBehavior.Connection, historyRepository.LockReleaseBehavior);
    }
}
