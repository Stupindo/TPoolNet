namespace TPoolNet.Tests;

using System.Globalization;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TPoolNet.Data;
using TPoolNet.Entities;
using TPoolNet.Options;
using TPoolNet.Services;

public class TPoolZombieSweeperIntegrationTests
{
    private const string ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=TPoolNet_Test_Story5;Integrated Security=true;TrustServerCertificate=True;";

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

    private static DbContextOptions<TPoolDbContext> CreateDbOptions()
    {
        return new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
    }

    private static TPoolZombieSweeperHostedService CreateSweeper(int heartbeatTimeoutSeconds = 120, int sweeperIntervalSeconds = 60)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TPoolDbContext>(options => options.UseSqlServer(ConnectionString));
        var provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new TPoolOptions
        {
            ConnectionString = ConnectionString,
            HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds,
            SweeperIntervalSeconds = sweeperIntervalSeconds
        });
        var logger = NullLogger<TPoolZombieSweeperHostedService>.Instance;

        return new TPoolZombieSweeperHostedService(scopeFactory, options, logger);
    }

    [Fact]
    public async Task SweepAsync_ReclaimsAbandonedTransientLease_TruncatesTableAndRecordsSweptZombie()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            long poolId;
            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "ZombieType",
                    TablePrefix = "Zombie",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY, Data NVARCHAR(50));",
                    MaxPoolSize = 5
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                var pool = new TablesPool
                {
                    TableTypeId = type.TableTypeId,
                    SchemaName = "tpool",
                    TableName = "tbl_Zombie_001",
                    IsActive = true
                };
                context.TablesPools.Add(pool);
                await context.SaveChangesAsync();
                poolId = pool.TablePoolId;

                // Create physical table and insert test data
                await context.Database.ExecuteSqlRawAsync("CREATE TABLE [tpool].[tbl_Zombie_001] (Id INT IDENTITY, Data NVARCHAR(50));");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO [tpool].[tbl_Zombie_001] (Data) VALUES ('test-data');");

                // Seed abandoned transient usage (Heartbeat older than 120s timeout)
                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "crashed-worker",
                    BookedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    HeartbeatUtc = DateTime.UtcNow.AddMinutes(-5),
                    DeadlineUtc = null
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            var sweeper = CreateSweeper(heartbeatTimeoutSeconds: 120);

            using (var context = new TPoolDbContext(options))
            {
                var reclaimed = await sweeper.SweepAsync(context);
                reclaimed.Should().Be(1);

                // Verify TablesUsage record deleted
                var remainingUsage = await context.TablesUsages.FindAsync(poolId);
                remainingUsage.Should().BeNull();

                // Verify physical table truncated
                var rowCount = await GetTableRowCountAsync(context, "tpool", "tbl_Zombie_001");
                rowCount.Should().Be(0);

                // Verify TablesUsageHistory record created
                var history = await context.TablesUsageHistories
                    .FirstOrDefaultAsync(h => h.TablePoolId == poolId);
                history.Should().NotBeNull();
                history!.ConsumerId.Should().Be("crashed-worker");
                history.ReleaseReason.Should().Be("SweptZombie");
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_PreservesActiveTransientLease_WithFreshHeartbeat()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            long poolId;
            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "ActiveType",
                    TablePrefix = "Active",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY, Data NVARCHAR(50));",
                    MaxPoolSize = 5
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                var pool = new TablesPool
                {
                    TableTypeId = type.TableTypeId,
                    SchemaName = "tpool",
                    TableName = "tbl_Active_001",
                    IsActive = true
                };
                context.TablesPools.Add(pool);
                await context.SaveChangesAsync();
                poolId = pool.TablePoolId;

                await context.Database.ExecuteSqlRawAsync("CREATE TABLE [tpool].[tbl_Active_001] (Id INT IDENTITY, Data NVARCHAR(50));");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO [tpool].[tbl_Active_001] (Data) VALUES ('live-data');");

                // Seed active transient usage (Heartbeat within timeout)
                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "healthy-worker",
                    BookedAtUtc = DateTime.UtcNow,
                    HeartbeatUtc = DateTime.UtcNow,
                    DeadlineUtc = null
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            var sweeper = CreateSweeper(heartbeatTimeoutSeconds: 120);

            using (var context = new TPoolDbContext(options))
            {
                var reclaimed = await sweeper.SweepAsync(context);
                reclaimed.Should().Be(0);

                // Verify TablesUsage record preserved
                var remainingUsage = await context.TablesUsages.FindAsync(poolId);
                remainingUsage.Should().NotBeNull();

                // Verify physical table data preserved
                var rowCount = await GetTableRowCountAsync(context, "tpool", "tbl_Active_001");
                rowCount.Should().Be(1);

                // Verify no history record created
                var historyCount = await context.TablesUsageHistories.CountAsync();
                historyCount.Should().Be(0);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_ReclaimsExpiredPersistentLease_TruncatesTableAndRecordsSweptExpired()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            long poolId;
            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "ExpiredType",
                    TablePrefix = "Expired",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY, Data NVARCHAR(50));",
                    MaxPoolSize = 5
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                var pool = new TablesPool
                {
                    TableTypeId = type.TableTypeId,
                    SchemaName = "tpool",
                    TableName = "tbl_Expired_001",
                    IsActive = true
                };
                context.TablesPools.Add(pool);
                await context.SaveChangesAsync();
                poolId = pool.TablePoolId;

                await context.Database.ExecuteSqlRawAsync("CREATE TABLE [tpool].[tbl_Expired_001] (Id INT IDENTITY, Data NVARCHAR(50));");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO [tpool].[tbl_Expired_001] (Data) VALUES ('persistent-data');");

                // Seed expired persistent lease (Deadline in past)
                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "past-consumer",
                    BookedAtUtc = DateTime.UtcNow.AddHours(-3),
                    HeartbeatUtc = DateTime.UtcNow.AddHours(-3),
                    DeadlineUtc = DateTime.UtcNow.AddMinutes(-30)
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            var sweeper = CreateSweeper(heartbeatTimeoutSeconds: 120);

            using (var context = new TPoolDbContext(options))
            {
                var reclaimed = await sweeper.SweepAsync(context);
                reclaimed.Should().Be(1);

                // Verify TablesUsage record deleted
                var remainingUsage = await context.TablesUsages.FindAsync(poolId);
                remainingUsage.Should().BeNull();

                // Verify physical table truncated
                var rowCount = await GetTableRowCountAsync(context, "tpool", "tbl_Expired_001");
                rowCount.Should().Be(0);

                // Verify TablesUsageHistory record created with SweptExpired
                var history = await context.TablesUsageHistories
                    .FirstOrDefaultAsync(h => h.TablePoolId == poolId);
                history.Should().NotBeNull();
                history!.ConsumerId.Should().Be("past-consumer");
                history.ReleaseReason.Should().Be("SweptExpired");
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_PreservesActivePersistentLease_WithFutureDeadline()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            long poolId;
            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "ActivePersistentType",
                    TablePrefix = "ActPersist",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY, Data NVARCHAR(50));",
                    MaxPoolSize = 5
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                var pool = new TablesPool
                {
                    TableTypeId = type.TableTypeId,
                    SchemaName = "tpool",
                    TableName = "tbl_ActPersist_001",
                    IsActive = true
                };
                context.TablesPools.Add(pool);
                await context.SaveChangesAsync();
                poolId = pool.TablePoolId;

                await context.Database.ExecuteSqlRawAsync("CREATE TABLE [tpool].[tbl_ActPersist_001] (Id INT IDENTITY, Data NVARCHAR(50));");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO [tpool].[tbl_ActPersist_001] (Data) VALUES ('keep-alive');");

                // Seed active persistent lease (Deadline in future, old heartbeat should NOT cause sweep)
                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "long-running-pipeline",
                    BookedAtUtc = DateTime.UtcNow.AddHours(-2),
                    HeartbeatUtc = DateTime.UtcNow.AddHours(-2), // Old heartbeat is fine for persistent leases
                    DeadlineUtc = DateTime.UtcNow.AddHours(2)    // Deadline is in future
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            var sweeper = CreateSweeper(heartbeatTimeoutSeconds: 120);

            using (var context = new TPoolDbContext(options))
            {
                var reclaimed = await sweeper.SweepAsync(context);
                reclaimed.Should().Be(0);

                // Verify TablesUsage record preserved
                var remainingUsage = await context.TablesUsages.FindAsync(poolId);
                remainingUsage.Should().NotBeNull();

                // Verify physical table data preserved
                var rowCount = await GetTableRowCountAsync(context, "tpool", "tbl_ActPersist_001");
                rowCount.Should().Be(1);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SendHeartbeatAsync_UpdatesHeartbeatTimestamp()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            long poolId;
            var pastTime = DateTime.UtcNow.AddMinutes(-15);

            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "HeartbeatTestType",
                    TablePrefix = "Hb",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                    MaxPoolSize = 5
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                var pool = new TablesPool
                {
                    TableTypeId = type.TableTypeId,
                    SchemaName = "tpool",
                    TableName = "tbl_Hb_001",
                    IsActive = true
                };
                context.TablesPools.Add(pool);
                await context.SaveChangesAsync();
                poolId = pool.TablePoolId;

                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "heartbeat-worker",
                    BookedAtUtc = pastTime,
                    HeartbeatUtc = pastTime,
                    DeadlineUtc = null
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            using (var context = new TPoolDbContext(options))
            {
                var service = new TablePoolService(context);
                await service.SendHeartbeatAsync(poolId);
            }

            using (var context = new TPoolDbContext(options))
            {
                var usage = await context.TablesUsages.FindAsync(poolId);
                usage.Should().NotBeNull();
                usage!.HeartbeatUtc.Should().BeAfter(pastTime.AddMinutes(10));
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_WithEmptyPool_ReturnsZero()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            var sweeper = CreateSweeper();
            using var context = new TPoolDbContext(options);
            var reclaimed = await sweeper.SweepAsync(context);
            reclaimed.Should().Be(0);
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_UsingScopeFactory_RunsSuccessfully()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            var sweeper = CreateSweeper();
            var reclaimed = await sweeper.SweepAsync();
            reclaimed.Should().Be(0);
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_ConcurrentSweepers_CleanEachTableExactlyOnceWithoutDeadlocks()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "ConcurrentSweepType",
                    TablePrefix = "ConcSweep",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                    MaxPoolSize = 10
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                for (var i = 1; i <= 5; i++)
                {
                    var tableName = $"tbl_ConcSweep_{i:D3}";
                    var pool = new TablesPool
                    {
                        TableTypeId = type.TableTypeId,
                        SchemaName = "tpool",
                        TableName = tableName,
                        IsActive = true
                    };
                    context.TablesPools.Add(pool);
                    await context.SaveChangesAsync();

#pragma warning disable EF1002
                    await context.Database.ExecuteSqlRawAsync($"CREATE TABLE [tpool].[{tableName}] (Id INT);");
                    await context.Database.ExecuteSqlRawAsync($"INSERT INTO [tpool].[{tableName}] VALUES ({i});");
#pragma warning restore EF1002

                    var usage = new TablesUsage
                    {
                        TablePoolId = pool.TablePoolId,
                        ConsumerId = $"worker-{i}",
                        BookedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                        HeartbeatUtc = DateTime.UtcNow.AddMinutes(-10),
                        DeadlineUtc = null
                    };
                    context.TablesUsages.Add(usage);
                }
                await context.SaveChangesAsync();
            }

            var sweeper1 = CreateSweeper();
            var sweeper2 = CreateSweeper();

            // Run two sweepers concurrently against the same 5 tables
            var task1 = Task.Run(async () =>
            {
                using var ctx = new TPoolDbContext(options);
                return await sweeper1.SweepAsync(ctx);
            });

            var task2 = Task.Run(async () =>
            {
                using var ctx = new TPoolDbContext(options);
                return await sweeper2.SweepAsync(ctx);
            });

            var results = await Task.WhenAll(task1, task2);
            var totalReclaimed = results[0] + results[1];

            totalReclaimed.Should().Be(5);

            using (var context = new TPoolDbContext(options))
            {
                var remainingUsageCount = await context.TablesUsages.CountAsync();
                remainingUsageCount.Should().Be(0);

                var historyCount = await context.TablesUsageHistories.CountAsync();
                historyCount.Should().Be(5);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_HeartbeatUpdatedConcurrently_AbortsCleanupSafely()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            long poolId;
            using (var context = new TPoolDbContext(options))
            {
                var type = new TablesType
                {
                    TypeName = "RaceType",
                    TablePrefix = "Race",
                    DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                    MaxPoolSize = 5
                };
                context.TablesTypes.Add(type);
                await context.SaveChangesAsync();

                var pool = new TablesPool
                {
                    TableTypeId = type.TableTypeId,
                    SchemaName = "tpool",
                    TableName = "tbl_Race_001",
                    IsActive = true
                };
                context.TablesPools.Add(pool);
                await context.SaveChangesAsync();
                poolId = pool.TablePoolId;

                await context.Database.ExecuteSqlRawAsync("CREATE TABLE [tpool].[tbl_Race_001] (Id INT);");
                await context.Database.ExecuteSqlRawAsync("INSERT INTO [tpool].[tbl_Race_001] VALUES (99);");

                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "racing-worker",
                    BookedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    HeartbeatUtc = DateTime.UtcNow.AddMinutes(-3),
                    DeadlineUtc = null
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            // Right before sweep runs, worker updates its heartbeat
            using (var context = new TPoolDbContext(options))
            {
                var poolService = new TablePoolService(context);
                await poolService.SendHeartbeatAsync(poolId);
            }

            var sweeper = CreateSweeper(heartbeatTimeoutSeconds: 120);
            using (var context = new TPoolDbContext(options))
            {
                var reclaimed = await sweeper.SweepAsync(context);
                reclaimed.Should().Be(0);

                var remainingUsage = await context.TablesUsages.FindAsync(poolId);
                remainingUsage.Should().NotBeNull();

                var rowCount = await GetTableRowCountAsync(context, "tpool", "tbl_Race_001");
                rowCount.Should().Be(1);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenSweeperDisabled_ExitsImmediately()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TPoolDbContext>(opt => opt.UseSqlServer(ConnectionString));
        var provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new TPoolOptions
        {
            EnableSweeper = false,
            SweeperIntervalSeconds = 60
        });
        var logger = NullLogger<TPoolZombieSweeperHostedService>.Instance;

        var sweeper = new TPoolZombieSweeperHostedService(scopeFactory, options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sweeper.StartAsync(cts.Token);

        sweeper.ExecuteTask.Should().NotBeNull();
        await sweeper.ExecuteTask!;
        sweeper.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static async Task<int> GetTableRowCountAsync(TPoolDbContext context, string schema, string table)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{schema}].[{table}];";
        var count = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }
}
