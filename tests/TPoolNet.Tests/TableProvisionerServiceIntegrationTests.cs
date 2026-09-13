namespace TPoolNet.Tests;

using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TPoolNet.Data;
using TPoolNet.Entities;
using TPoolNet.Exceptions;
using TPoolNet.Services;

public class TableProvisionerServiceIntegrationTests
{
    private const string ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=TPoolNet_Test_Story4;Integrated Security=true;TrustServerCertificate=True;";

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

    [Fact]
    public async Task ProvisionTablesAsync_NonExistentType_ThrowsTableTypeNotFoundException()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        try
        {
            using var context = new TPoolDbContext(options);
            var service = new TableProvisionerService(context);

            var act = () => service.ProvisionTablesAsync("MissingType", 1);
            await act.Should().ThrowAsync<TableTypeNotFoundException>()
                .Where(e => e.TableTypeName == "MissingType");
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProvisionTablesAsync_ValidTemplate_CreatesPhysicalTablesAndPoolEntries()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "OrderBatch",
                TablePrefix = "Order",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY PRIMARY KEY, CustomerName NVARCHAR(100) NOT NULL);",
                MaxPoolSize = 10
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.ProvisionTablesAsync("OrderBatch", 2);
            }

            // Verify pool entries and physical tables exist
            using (var context = new TPoolDbContext(options))
            {
                var pools = await context.TablesPools
                    .Where(p => p.TableType.TypeName == "OrderBatch")
                    .OrderBy(p => p.TableName)
                    .ToListAsync();

                pools.Should().HaveCount(2);
                pools[0].TableName.Should().Be("tbl_Order_001");
                pools[0].SchemaName.Should().Be("tpool");
                pools[0].IsActive.Should().BeTrue();
                pools[1].TableName.Should().Be("tbl_Order_002");
                pools[1].SchemaName.Should().Be("tpool");

                // Check physical table existence in SQL Server
                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OBJECT_ID('tpool.tbl_Order_001', 'U'), OBJECT_ID('tpool.tbl_Order_002', 'U');";
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.IsDBNull(0).Should().BeFalse();
                reader.IsDBNull(1).Should().BeFalse();
            }

            // Provision 1 more and verify sequence continues to 003
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.ProvisionTablesAsync("OrderBatch", 1);
            }

            using (var context = new TPoolDbContext(options))
            {
                var pools = await context.TablesPools
                    .Where(p => p.TableType.TypeName == "OrderBatch")
                    .OrderBy(p => p.TableName)
                    .ToListAsync();

                pools.Should().HaveCount(3);
                pools[2].TableName.Should().Be("tbl_Order_003");

                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OBJECT_ID('tpool.tbl_Order_003', 'U');";
                var objId = await cmd.ExecuteScalarAsync();
                objId.Should().NotBeNull();
                objId.Should().NotBe(DBNull.Value);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProvisionTablesAsync_ExceedingMaxPoolSize_ThrowsPoolCapacityExceededException()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "LimitedPool",
                TablePrefix = "Limit",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 3
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.ProvisionTablesAsync("LimitedPool", 2);
            }

            // Attempting to provision 2 more when capacity is 3 (2+2 = 4 > 3) should throw
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                var act = () => service.ProvisionTablesAsync("LimitedPool", 2);
                await act.Should().ThrowAsync<PoolCapacityExceededException>()
                    .Where(e => e.TableTypeName == "LimitedPool" &&
                                e.RequestedCount == 2 &&
                                e.CurrentCount == 2 &&
                                e.MaxPoolSize == 3);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task EnsurePoolCapacityAsync_ProvisionsMissingTablesAndIsIdempotent()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "CapacityType",
                TablePrefix = "Cap",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 10
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            // Initial call: target capacity 3 -> provisions 3 tables
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                var provisioned = await service.EnsurePoolCapacityAsync("CapacityType", 3);
                provisioned.Should().Be(3);
            }

            // Verify 3 pool rows
            using (var context = new TPoolDbContext(options))
            {
                var count = await context.TablesPools.CountAsync(p => p.TableType.TypeName == "CapacityType");
                count.Should().Be(3);
            }

            // Idempotent call: target capacity 3 when 3 already exist -> provisions 0
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                var provisioned = await service.EnsurePoolCapacityAsync("CapacityType", 3);
                provisioned.Should().Be(0);
            }

            // Increase capacity: target capacity 5 -> provisions 2 more
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                var provisioned = await service.EnsurePoolCapacityAsync("CapacityType", 5);
                provisioned.Should().Be(2);
            }

            using (var context = new TPoolDbContext(options))
            {
                var count = await context.TablesPools.CountAsync(p => p.TableType.TypeName == "CapacityType");
                count.Should().Be(5);
            }

            // Target capacity exceeding MaxPoolSize throws PoolCapacityExceededException
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                var act = () => service.EnsurePoolCapacityAsync("CapacityType", 15);
                await act.Should().ThrowAsync<PoolCapacityExceededException>()
                    .Where(e => e.TableTypeName == "CapacityType" && e.MaxPoolSize == 10);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task DropTablesAsync_WithoutForce_DropsOnlyUnusedTables()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "DropType",
                TablePrefix = "Drop",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 10
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.ProvisionTablesAsync("DropType", 3);
            }

            // Book tbl_Drop_001
            long bookedPoolId;
            using (var context = new TPoolDbContext(options))
            {
                var pool1 = await context.TablesPools.FirstAsync(p => p.TableName == "tbl_Drop_001");
                bookedPoolId = pool1.TablePoolId;

                var usage = new TablesUsage
                {
                    TablePoolId = bookedPoolId,
                    ConsumerId = "worker-1",
                    BookedAtUtc = DateTime.UtcNow,
                    HeartbeatUtc = DateTime.UtcNow
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            // Request dropping 2 unused tables (tbl_Drop_003 and tbl_Drop_002)
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.DropTablesAsync("DropType", 2, force: false);
            }

            // Verify only tbl_Drop_001 remains in TablesPool
            using (var context = new TPoolDbContext(options))
            {
                var remaining = await context.TablesPools
                    .Where(p => p.TableType.TypeName == "DropType")
                    .ToListAsync();

                remaining.Should().HaveCount(1);
                remaining[0].TableName.Should().Be("tbl_Drop_001");

                // Verify physical tables dropped
                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OBJECT_ID('tpool.tbl_Drop_002', 'U'), OBJECT_ID('tpool.tbl_Drop_003', 'U'), OBJECT_ID('tpool.tbl_Drop_001', 'U');";
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.IsDBNull(0).Should().BeTrue(); // tbl_Drop_002 dropped
                reader.IsDBNull(1).Should().BeTrue(); // tbl_Drop_003 dropped
                reader.IsDBNull(2).Should().BeFalse(); // tbl_Drop_001 still exists
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task DropTablesAsync_WithForce_DropsBookedTablesAndRecordsHistory()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "ForceDropType",
                TablePrefix = "FDrop",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.ProvisionTablesAsync("ForceDropType", 1);
            }

            long poolId;
            using (var context = new TPoolDbContext(options))
            {
                var pool = await context.TablesPools.FirstAsync(p => p.TableName == "tbl_FDrop_001");
                poolId = pool.TablePoolId;

                var usage = new TablesUsage
                {
                    TablePoolId = poolId,
                    ConsumerId = "active-worker",
                    BookedAtUtc = DateTime.UtcNow,
                    HeartbeatUtc = DateTime.UtcNow
                };
                context.TablesUsages.Add(usage);
                await context.SaveChangesAsync();
            }

            // Force drop the table even though it's active
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.DropTablesAsync("ForceDropType", 1, force: true);
            }

            // Verify pool is empty, usage deleted, history inserted, physical table dropped
            using (var context = new TPoolDbContext(options))
            {
                var pools = await context.TablesPools.Where(p => p.TableType.TypeName == "ForceDropType").ToListAsync();
                pools.Should().BeEmpty();

                var usages = await context.TablesUsages.Where(u => u.TablePoolId == poolId).ToListAsync();
                usages.Should().BeEmpty();

                var history = await context.TablesUsageHistories.FirstOrDefaultAsync(h => h.TablePoolId == poolId);
                history.Should().NotBeNull();
                history!.ConsumerId.Should().Be("active-worker");
                history.ReleaseReason.Should().Be("Dropped");

                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OBJECT_ID('tpool.tbl_FDrop_001', 'U');";
                var objId = await cmd.ExecuteScalarAsync();
                objId.Should().Be(DBNull.Value);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProvisionTablesAsync_TemplateMissingTableNamePlaceholder_ThrowsInvalidOperationException()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "BrokenTemplateType",
                TablePrefix = "Broken",
                DdlTemplate = "CREATE TABLE [tpool].[HardcodedTable] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                var act = () => service.ProvisionTablesAsync("BrokenTemplateType", 1);
                await act.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*does not contain '{TABLENAME}' placeholder*");
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProvisionTablesAsync_SupportsLegacyPlaceholders()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "LegacyTemplateType",
                TablePrefix = "Leg",
                DdlTemplate = "CREATE TABLE [<SchemaName>].[<TableName>] (Id INT PRIMARY KEY);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.ProvisionTablesAsync("LegacyTemplateType", 1);
            }

            using (var context = new TPoolDbContext(options))
            {
                var pool = await context.TablesPools.FirstOrDefaultAsync(p => p.TableName == "tbl_Leg_001");
                pool.Should().NotBeNull();

                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT OBJECT_ID('tpool.tbl_Leg_001', 'U');";
                var objId = await cmd.ExecuteScalarAsync();
                objId.Should().NotBe(DBNull.Value);
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task DropTablesAsync_WithForce_PrioritizesUnusedTablesOverActiveTables()
    {
        if (!CanConnectToLocalDb()) return;

        var options = CreateDbOptions();
        using (var context = new TPoolDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var type = new TablesType
            {
                TypeName = "PriorityDropType",
                TablePrefix = "PDrop",
                DdlTemplate = "CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT);",
                MaxPoolSize = 5
            };
            context.TablesTypes.Add(type);
            await context.SaveChangesAsync();
        }

        try
        {
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                // Provision 3 tables: 001, 002, 003
                await service.ProvisionTablesAsync("PriorityDropType", 3);
            }

            // Book tbl_PDrop_003 (newest table)
            using (var context = new TPoolDbContext(options))
            {
                var table3 = await context.TablesPools.FirstAsync(p => p.TableName == "tbl_PDrop_003");
                context.TablesUsages.Add(new TablesUsage
                {
                    TablePoolId = table3.TablePoolId,
                    ConsumerId = "active-user",
                    BookedAtUtc = DateTime.UtcNow,
                    HeartbeatUtc = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }

            // Drop 1 table with force = true
            // Should drop unused table (tbl_PDrop_002) first, NOT the active tbl_PDrop_003!
            using (var context = new TPoolDbContext(options))
            {
                var service = new TableProvisionerService(context);
                await service.DropTablesAsync("PriorityDropType", 1, force: true);
            }

            // Verify active table 003 was spared and remains booked, table 002 was dropped
            using (var context = new TPoolDbContext(options))
            {
                var remaining = await context.TablesPools
                    .Where(p => p.TableType.TypeName == "PriorityDropType")
                    .OrderBy(p => p.TableName)
                    .ToListAsync();

                remaining.Should().HaveCount(2);
                remaining.Select(r => r.TableName).Should().Contain("tbl_PDrop_001");
                remaining.Select(r => r.TableName).Should().Contain("tbl_PDrop_003");
                remaining.Select(r => r.TableName).Should().NotContain("tbl_PDrop_002");

                var usage = await context.TablesUsages.FirstOrDefaultAsync(u => u.ConsumerId == "active-user");
                usage.Should().NotBeNull();
            }
        }
        finally
        {
            using var context = new TPoolDbContext(options);
            await context.Database.EnsureDeletedAsync();
        }
    }
}
