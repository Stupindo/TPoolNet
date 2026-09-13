namespace TPoolNet.Tests;

using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TPoolNet.Data;
using TPoolNet.Entities;
using TPoolNet.Exceptions;
using TPoolNet.Services;

public class TablePoolServiceReleaseIntegrationTests
{
    private const string ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=TPoolNet_Test_Story3;Integrated Security=true;TrustServerCertificate=True;";

    private static bool CanConnectToLocalDb()
    {
        try
        {
            using var conn = new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=True;Connect Timeout=2;");
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task ReleaseAsync_TableLeaseNotFound_ThrowsTableLeaseNotFoundException()
    {
        if (!CanConnectToLocalDb()) return;

        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        using (var context = new TPoolDbContext(options))
        {
            var service = new TablePoolService(context);
            var act = () => service.ReleaseAsync("tpool.tbl_NonExistent_001", "consumer-1");
            await act.Should().ThrowAsync<TableLeaseNotFoundException>()
                .Where(e => e.SchemaName == "tpool" && e.TableName == "tbl_NonExistent_001");
        }

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ReleaseAsync_ConsumerMismatch_ThrowsConsumerMismatchException()
    {
        if (!CanConnectToLocalDb()) return;

        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            // Seed TablesType, TablesPool, and TablesUsage
            var type = new TablesType
            {
                TypeName = "MismatchTestType",
                TablePrefix = "Mismatch",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();

            var pool = new TablesPool
            {
                TableTypeId = type.TableTypeId,
                SchemaName = "tpool",
                TableName = "tbl_Mismatch_001",
                IsActive = true
            };
            context.TablesPools.Add(pool);
            await context.SaveChangesAsync();

            var usage = new TablesUsage
            {
                TablePoolId = pool.TablePoolId,
                ConsumerId = "actual-owner",
                BookedAtUtc = DateTime.UtcNow,
                HeartbeatUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow.AddHours(1)
            };
            context.TablesUsages.Add(usage);
            await context.SaveChangesAsync();
        }

        using (var context = new TPoolDbContext(options))
        {
            var service = new TablePoolService(context);
            var act = () => service.ReleaseAsync("tpool.tbl_Mismatch_001", "imposter-consumer");
            await act.Should().ThrowAsync<ConsumerMismatchException>()
                .Where(e => e.SchemaName == "tpool" &&
                            e.TableName == "tbl_Mismatch_001" &&
                            e.ExpectedConsumerId == "imposter-consumer" &&
                            e.ActualConsumerId == "actual-owner");
        }

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ReleaseAsync_ValidConsumer_TruncatesTableInsertsHistoryAndDeletesUsage()
    {
        if (!CanConnectToLocalDb()) return;

        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        long tablePoolId;

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            // Create physical table [tpool].[tbl_Persist_001]
            await context.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID('tpool.tbl_Persist_001', 'U') IS NOT NULL DROP TABLE [tpool].[tbl_Persist_001]; " +
                "CREATE TABLE [tpool].[tbl_Persist_001] (Id INT IDENTITY, Data NVARCHAR(100));");

            // Seed TablesType, TablesPool, and TablesUsage
            var type = new TablesType
            {
                TypeName = "PersistTestType",
                TablePrefix = "Persist",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();

            var pool = new TablesPool
            {
                TableTypeId = type.TableTypeId,
                SchemaName = "tpool",
                TableName = "tbl_Persist_001",
                IsActive = true
            };
            context.TablesPools.Add(pool);
            await context.SaveChangesAsync();
            tablePoolId = pool.TablePoolId;

            var usage = new TablesUsage
            {
                TablePoolId = pool.TablePoolId,
                ConsumerId = "stage-1-worker",
                BookedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                HeartbeatUtc = DateTime.UtcNow.AddMinutes(-5),
                DeadlineUtc = DateTime.UtcNow.AddHours(2)
            };
            context.TablesUsages.Add(usage);
            await context.SaveChangesAsync();

            // Insert sample data into the physical table
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO [tpool].[tbl_Persist_001] (Data) VALUES ('Row 1'), ('Row 2'), ('Row 3');");
        }

        // Verify rows exist before release
        using (var context = new TPoolDbContext(options))
        {
            var countBefore = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [tpool].[tbl_Persist_001]").FirstAsync();
            countBefore.Should().Be(3);

            // Execute explicit release
            var service = new TablePoolService(context);
            await service.ReleaseAsync("[tpool].[tbl_Persist_001]", "stage-1-worker");
        }

        // Verify post-release state: table truncated, usage deleted, history created
        using (var context = new TPoolDbContext(options))
        {
            // 1. Physical table truncated
            var countAfter = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [tpool].[tbl_Persist_001]").FirstAsync();
            countAfter.Should().Be(0);

            // 2. Active usage deleted
            var activeUsage = await context.TablesUsages
                .FirstOrDefaultAsync(u => u.TablePoolId == tablePoolId);
            activeUsage.Should().BeNull();

            // 3. History recorded with "Explicit" reason
            var history = await context.TablesUsageHistories
                .FirstOrDefaultAsync(h => h.TablePoolId == tablePoolId);
            history.Should().NotBeNull();
            history!.ConsumerId.Should().Be("stage-1-worker");
            history.ReleaseReason.Should().Be("Explicit");

            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ReleaseAsync_MissingOrAlreadyReleasedLease_NeverTruncatesPhysicalTable()
    {
        if (!CanConnectToLocalDb()) return;

        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            // Create physical table with data
            await context.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID('tpool.tbl_Unleased_001', 'U') IS NOT NULL DROP TABLE [tpool].[tbl_Unleased_001]; " +
                "CREATE TABLE [tpool].[tbl_Unleased_001] (Id INT IDENTITY, Data NVARCHAR(100)); " +
                "INSERT INTO [tpool].[tbl_Unleased_001] (Data) VALUES ('Important 1'), ('Important 2');");

            var type = new TablesType
            {
                TypeName = "UnleasedTestType",
                TablePrefix = "Unleased",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();

            var pool = new TablesPool
            {
                TableTypeId = type.TableTypeId,
                SchemaName = "tpool",
                TableName = "tbl_Unleased_001",
                IsActive = true
            };
            context.TablesPools.Add(pool);
            await context.SaveChangesAsync();
        }

        using (var context = new TPoolDbContext(options))
        {
            var service = new TablePoolService(context);

            // Attempt to release when no active TablesUsage exists
            var act = () => service.ReleaseAsync("[tpool].[tbl_Unleased_001]", "any-consumer");
            await act.Should().ThrowAsync<TableLeaseNotFoundException>();

            // Assert physical table rows were NOT truncated
            var count = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [tpool].[tbl_Unleased_001]").FirstAsync();
            count.Should().Be(2);

            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task BookPersistentAsync_FullLifecycle_RetainsDataAcrossDisposalAndRequiresMatchingConsumerForRelease()
    {
        if (!CanConnectToLocalDb()) return;

        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            // Create physical table [tpool].[tbl_Lifecycle_001]
            await context.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID('tpool.tbl_Lifecycle_001', 'U') IS NOT NULL DROP TABLE [tpool].[tbl_Lifecycle_001]; " +
                "CREATE TABLE [tpool].[tbl_Lifecycle_001] (Id INT IDENTITY, Data NVARCHAR(100));");

            // Seed TablesType and TablesPool
            var type = new TablesType
            {
                TypeName = "LifecycleTestType",
                TablePrefix = "Lifecycle",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();

            var pool = new TablesPool
            {
                TableTypeId = type.TableTypeId,
                SchemaName = "tpool",
                TableName = "tbl_Lifecycle_001",
                IsActive = true
            };
            context.TablesPools.Add(pool);
            await context.SaveChangesAsync();
        }

        string fullQualifiedName;
        long tablePoolId;

        // Stage 1: Book persistent lease and insert physical data
        using (var context = new TPoolDbContext(options))
        {
            var service = new TablePoolService(context);
            var retentionPeriod = TimeSpan.FromHours(2);

            var lease = await service.BookPersistentAsync("LifecycleTestType", "consumer-stage-1", retentionPeriod);

            lease.Should().NotBeNull();
            lease.IsPersistent.Should().BeTrue();
            lease.DeadlineUtc.Should().NotBeNull();
            lease.DeadlineUtc!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(110));
            lease.ConsumerId.Should().Be("consumer-stage-1");
            lease.TableName.Should().Be("tbl_Lifecycle_001");
            lease.SchemaName.Should().Be("tpool");

            fullQualifiedName = lease.FullQualifiedName;
            tablePoolId = lease.TablePoolId;

            // Insert data while lease is held
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO [tpool].[tbl_Lifecycle_001] (Data) VALUES ('Lifecycle Data 1'), ('Lifecycle Data 2');");

            // Dispose the lease (simulating process termination)
            await lease.DisposeAsync();
        }

        // Verify data and usage persist across lease disposal
        using (var context = new TPoolDbContext(options))
        {
            var countAfterDisposal = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [tpool].[tbl_Lifecycle_001]").FirstAsync();
            countAfterDisposal.Should().Be(2, "persistent lease data must survive lease disposal");

            var usage = await context.TablesUsages.FirstOrDefaultAsync(u => u.TablePoolId == tablePoolId);
            usage.Should().NotBeNull("persistent lease usage record must survive lease disposal");
            usage!.ConsumerId.Should().Be("consumer-stage-1");
            usage.DeadlineUtc.Should().NotBeNull();

            var history = await context.TablesUsageHistories.FirstOrDefaultAsync(h => h.TablePoolId == tablePoolId);
            history.Should().BeNull("no release history should exist while persistent lease is active");
        }

        // Stage 2: Mismatched consumer cannot release
        using (var context = new TPoolDbContext(options))
        {
            var service = new TablePoolService(context);

            var act = () => service.ReleaseAsync(fullQualifiedName, "imposter-stage-2");
            await act.Should().ThrowAsync<ConsumerMismatchException>();

            var countAfterFailedRelease = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [tpool].[tbl_Lifecycle_001]").FirstAsync();
            countAfterFailedRelease.Should().Be(2, "failed release with mismatched consumer must not truncate data");
        }

        // Stage 3: Authorized consumer explicitly releases
        using (var context = new TPoolDbContext(options))
        {
            var service = new TablePoolService(context);
            await service.ReleaseAsync(fullQualifiedName, "consumer-stage-1");

            // Verify table is truncated
            var countAfterRelease = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [tpool].[tbl_Lifecycle_001]").FirstAsync();
            countAfterRelease.Should().Be(0, "explicit release must truncate physical table");

            // Verify usage deleted
            var activeUsage = await context.TablesUsages.FirstOrDefaultAsync(u => u.TablePoolId == tablePoolId);
            activeUsage.Should().BeNull("explicit release must delete active usage record");

            // Verify history recorded
            var history = await context.TablesUsageHistories.FirstOrDefaultAsync(h => h.TablePoolId == tablePoolId);
            history.Should().NotBeNull("explicit release must log history");
            history!.ConsumerId.Should().Be("consumer-stage-1");
            history.ReleaseReason.Should().Be("Explicit");

            await context.Database.EnsureDeletedAsync();
        }
    }
}
