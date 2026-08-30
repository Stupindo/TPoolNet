# TPoolNet: Architecture Specification & Phased Implementation Plan

## 1. System Overview & Core Objectives

**TPoolNet** is a high-performance .NET 8+ class library designed to eliminate SQL Server performance bottlenecks caused by frequent `#temp` table creation—specifically metadata catalog contention, DDL compilation locks, and `tempdb` allocation stalls.

The library replaces the legacy T-SQL stored-procedure-based TPool framework by moving all lifecycle, leasing, and dynamic table generation logic into C# and Entity Framework Core, while preserving only the necessary configuration and metadata tables in the database.

```text
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│                                   TPoolNet Architecture                                 │
│                                                                                          │
│   ┌──────────────────────────────────────────────────────────────────────────────────┐   │
│   │                                Application Layer                                 │   │
│   │   - Transient Consumer: await using var lease = await pool.BookAsync(...)        │   │
│   │   - Persistent Consumer: var lease = await pool.BookPersistentAsync(...)         │   │
│   └────────────────────────────────────────┬─────────────────────────────────────────┘   │
│                                            │                                             │
│                                            ▼                                             │
│   ┌──────────────────────────────────────────────────────────────────────────────────┐   │
│   │                        TPoolNet Core Service Engine                              │   │
│   │  ┌───────────────────────┐ ┌──────────────────────────┐ ┌──────────────────────┐  │   │
│   │  │   TablePoolService    │ │    TableProvisioner      │ │ TPoolZombieSweeper   │  │   │
│   │  │  (Atomic CTE Leasing) │ │ (Dynamic DDL Generator)  │ │ (Background Service) │  │   │
│   │  └───────────────────────┘ └──────────────────────────┘ └──────────────────────┘  │   │
│   └────────────────────────────────────────┬─────────────────────────────────────────┘   │
│                                            │ EF Core + Raw Parameterized SQL             │
│                                            ▼                                             │
│   ┌──────────────────────────────────────────────────────────────────────────────────┐   │
│   │                              SQL Server Database                                 │   │
│   │  ┌──────────────────────────────────────────────┐ ┌───────────────────────────┐  │   │
│   │  │  [tpool] Metadata Tables                     │ │ Physical Pooled Tables    │  │   │
│   │  │  - TablesType, TablesPool, TablesUsage, ...  │ │ - [tpool].[tbl_Order_001] │  │   │
│   │  └──────────────────────────────────────────────┘ └───────────────────────────┘  │   │
│   └──────────────────────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

### Key Functional Requirements

* **Legacy Stored Procedure & UDF Elimination**: Remove `sproc_BookTableFromTablesPool`, `sproc_ReleaseTablesPool`, `sproc_AddTablesPool`, `sproc_DropTablesPool`, `sproc_DropUnusedTablesPool`, `sproc_GenerateTableScript`, `sproc_GetCurrentTablesUsage`, `udf_GetPoolTableName`, `udf_GetTablesPoolByConsumer`, and `udf_GetTablesTypeId` in favor of compiled C# services and LINQ/raw SQL execution.
* **Two Distinct Leasing Modes**:
  1. *Transient Mode*: Short-lived lease implementing `IAsyncDisposable`. Scope exit automatically truncates the physical table and returns the slot to the pool.
  2. *Persistent Mode*: Multi-stage or cross-process lease. The table data survives consumer application termination. The table remains held until explicit release via API or until a configured `DeadlineUtc` is reached.
* **Atomic Concurrency Engine**: Safe multi-threaded table reservation using SQL Server locking hints (`UPDLOCK, ROWLOCK, READPAST`) to prevent deadlocks and race conditions under high concurrent demand.
* **Background Zombie & Deadline Sweeper**: Hosted background service (`IHostedService`) monitoring heartbeats for crashed transient workers and deadlines for expired persistent stages.

---

## 2. Database Schema & EF Core Model

All metadata tables reside in the dedicated `[tpool]` database schema.

```text
┌─────────────────────────────────┐
│     [tpool].[TablesType]        │
├─────────────────────────────────┤
│ PK  TableTypeId     INT         │
│     TypeName        VARCHAR(50) │
│     TablePrefix     VARCHAR(50) │
│     DdlTemplate    NVARCHAR(MAX)│
│     MaxPoolSize     INT         │
└────────────────┬────────────────┘
                 │ 1
                 │
                 │ *
┌────────────────┴────────────────┐
│     [tpool].[TablesPool]        │
├─────────────────────────────────┤
│ PK  TablePoolId     INT         │
│ FK  TableTypeId     INT         │
│     SchemaName      SYSNAME     │
│     TableName       SYSNAME     │
│     IsActive        BIT         │
│     CreatedAtUtc    DATETIME2   │
└────────────────┬────────────────┘
                 │ 1
                 │
                 │ 0..1
┌────────────────┴────────────────┐
│     [tpool].[TablesUsage]       │
├─────────────────────────────────┤
│ PK  TablePoolId     INT         │
│     ConsumerId      VARCHAR(100)│
│     BookedAtUtc     DATETIME2   │
│     HeartbeatUtc    DATETIME2   │
│     DeadlineUtc     DATETIME2?  │ <── NULL = Transient; Non-NULL = Persistent
└─────────────────────────────────┘
```

### 2.1 Entity Classes

```csharp
namespace TPoolNet.Entities;

public class TablesType
{
    public int TableTypeId { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string TablePrefix { get; set; } = string.Empty;
    public string DdlTemplate { get; set; } = string.Empty;
    public int MaxPoolSize { get; set; }

    public virtual ICollection<TablesPool> Tables { get; set; } = new List<TablesPool>();
}

public class TablesConsumerType
{
    public int ConsumerTypeId { get; set; }
    public string ConsumerName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class TablesPool
{
    public int TablePoolId { get; set; }
    public int TableTypeId { get; set; }
    public string SchemaName { get; set; } = "tpool";
    public string TableName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public virtual TablesType TableType { get; set; } = null!;
    public virtual TablesUsage? ActiveUsage { get; set; }
}

public class TablesUsage
{
    public int TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public DateTime? DeadlineUtc { get; set; }

    public virtual TablesPool TablePool { get; set; } = null!;
}

public class TablesUsageHistory
{
    public long HistoryId { get; set; }
    public int TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime ReleasedAtUtc { get; set; }
    public string ReleaseReason { get; set; } = "Explicit"; // "Explicit", "Disposed", "SweptZombie", "SweptExpired"
}
```

### 2.2 Fluent Configuration (TPoolDbContext)

```csharp
namespace TPoolNet.Data;

using Microsoft.EntityFrameworkCore;
using TPoolNet.Entities;

public class TPoolDbContext : DbContext
{
    public TPoolDbContext(DbContextOptions<TPoolDbContext> options) : base(options) { }

    public DbSet<TablesType> TablesTypes => Set<TablesType>();
    public DbSet<TablesConsumerType> TablesConsumerTypes => Set<TablesConsumerType>();
    public DbSet<TablesPool> TablesPools => Set<TablesPool>();
    public DbSet<TablesUsage> TablesUsages => Set<TablesUsage>();
    public DbSet<TablesUsageHistory> TablesUsageHistories => Set<TablesUsageHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("tpool");

        modelBuilder.Entity<TablesType>(b =>
        {
            b.ToTable("TablesType");
            b.HasKey(e => e.TableTypeId);
            b.Property(e => e.TypeName).HasMaxLength(50).IsRequired();
            b.HasIndex(e => e.TypeName).IsUnique();
            b.Property(e => e.TablePrefix).HasMaxLength(50).IsRequired();
            b.Property(e => e.DdlTemplate).IsRequired();
            b.Property(e => e.MaxPoolSize).IsRequired();
        });

        modelBuilder.Entity<TablesConsumerType>(b =>
        {
            b.ToTable("TablesConsumerType");
            b.HasKey(e => e.ConsumerTypeId);
            b.Property(e => e.ConsumerName).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<TablesPool>(b =>
        {
            b.ToTable("TablesPool");
            b.HasKey(e => e.TablePoolId);
            b.Property(e => e.SchemaName).HasMaxLength(128).HasDefaultValue("tpool");
            b.Property(e => e.TableName).HasMaxLength(128).IsRequired();
            b.HasIndex(e => new { e.SchemaName, e.TableName }).IsUnique();

            b.HasOne(e => e.TableType)
             .WithMany(t => t.Tables)
             .HasForeignKey(e => e.TableTypeId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TablesUsage>(b =>
        {
            b.ToTable("TablesUsage");
            b.HasKey(e => e.TablePoolId);
            b.Property(e => e.ConsumerId).HasMaxLength(100).IsRequired();
            b.Property(e => e.BookedAtUtc).HasPrecision(2);
            b.Property(e => e.HeartbeatUtc).HasPrecision(2);
            b.Property(e => e.DeadlineUtc).HasPrecision(2);

            b.HasOne(e => e.TablePool)
             .WithOne(p => p.ActiveUsage)
             .HasForeignKey<TablesUsage>(e => e.TablePoolId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TablesUsageHistory>(b =>
        {
            b.ToTable("TablesUsageHistory");
            b.HasKey(e => e.HistoryId);
            b.Property(e => e.ConsumerId).HasMaxLength(100).IsRequired();
            b.Property(e => e.ReleaseReason).HasMaxLength(50).IsRequired();
            b.HasIndex(e => e.TablePoolId);
        });
    }
}
```

---

## 3. Core Mechanics & Algorithms

### 3.1 Atomic Booking Query

To prevent concurrency conflicts across distributed workers, table reservation uses a Common Table Expression (CTE) executed with locking hints in a single atomic SQL roundtrip:

```sql
WITH AvailableTable AS (
    SELECT TOP (1) p.TablePoolId, p.SchemaName, p.TableName
    FROM [tpool].[TablesPool] p WITH (ROWLOCK, READPAST)
    INNER JOIN [tpool].[TablesType] t ON p.TableTypeId = t.TableTypeId
    WHERE t.TypeName = @TableTypeName 
      AND p.IsActive = 1
      AND NOT EXISTS (
          SELECT 1 
          FROM [tpool].[TablesUsage] u WITH (UPDLOCK, ROWLOCK, READPAST)
          WHERE u.TablePoolId = p.TablePoolId
      )
    ORDER BY p.TablePoolId ASC
)
INSERT INTO [tpool].[TablesUsage] (TablePoolId, ConsumerId, BookedAtUtc, HeartbeatUtc, DeadlineUtc)
OUTPUT 
    inserted.TablePoolId, 
    inserted.ConsumerId, 
    inserted.BookedAtUtc, 
    inserted.DeadlineUtc,
    (SELECT SchemaName FROM AvailableTable) AS SchemaName,
    (SELECT TableName FROM AvailableTable) AS TableName
SELECT TablePoolId, @ConsumerId, SYSUTCDATETIME(), SYSUTCDATETIME(), @DeadlineUtc
FROM AvailableTable;
```

### 3.2 Dynamic Table Provisioner

Replaces `sproc_AddTablesPool` and `sproc_GenerateTableScript`:
* Fetches the `DdlTemplate` from `TablesType` (e.g., `CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY, Payload NVARCHAR(MAX), CreatedAt DATETIME2)`).
* Calculates next sequence index for naming: `tbl_{TablePrefix}_{Sequence:D3}`.
* Generates and executes DDL via `Database.ExecuteSqlRawAsync(...)`.
* Registers the newly created physical table into `[tpool].[TablesPool]` inside a local transaction.

### 3.3 Lease Lifecycle Engine (Transient vs. Persistent)

```csharp
namespace TPoolNet.Services;

using TPoolNet.Abstractions;

public class PooledTableLease : IPooledTableLease
{
    private readonly Func<PooledTableLease, string, Task> _releaseAction;
    private bool _isDisposed;

    public int TablePoolId { get; init; }
    public string SchemaName { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string ConsumerId { get; init; } = string.Empty;
    public DateTime BookedAtUtc { get; init; }
    public DateTime? DeadlineUtc { get; init; }
    public bool IsPersistent => DeadlineUtc.HasValue;
    public string FullQualifiedName => $"[{SchemaName}].[{TableName}]";

    public PooledTableLease(Func<PooledTableLease, string, Task> releaseAction)
    {
        _releaseAction = releaseAction;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (!IsPersistent)
        {
            // Transient leases auto-release on scope disposal
            await _releaseAction(this, "Disposed");
        }
        // Persistent leases deliberately do NOT truncate or release on DisposeAsync.
    }
}
```

### 3.4 Sweeper Background Engine

The `TPoolZombieSweeperHostedService` periodically executes a set-based cleanup query:

```sql
-- Find expired persistent leases or dead transient workers
SELECT u.TablePoolId, p.SchemaName, p.TableName, u.ConsumerId, u.BookedAtUtc,
       CASE 
           WHEN u.DeadlineUtc IS NOT NULL AND u.DeadlineUtc < SYSUTCDATETIME() THEN 'SweptExpired'
           ELSE 'SweptZombie'
       END AS CleanupReason
FROM [tpool].[TablesUsage] u WITH (UPDLOCK, READPAST)
JOIN [tpool].[TablesPool] p ON u.TablePoolId = p.TablePoolId
WHERE 
    (u.DeadlineUtc IS NOT NULL AND u.DeadlineUtc < SYSUTCDATETIME())
    OR 
    (u.DeadlineUtc IS NULL AND u.HeartbeatUtc < DATEADD(second, -@HeartbeatTimeoutSeconds, SYSUTCDATETIME()));
```

For every identified record:
* `TRUNCATE TABLE [{SchemaName}].[{TableName}]`
* `INSERT INTO [tpool].[TablesUsageHistory]` logging duration and `CleanupReason`.
* `DELETE FROM [tpool].[TablesUsage] WHERE TablePoolId = @TablePoolId`.

---

## 4. Public API & Interfaces

```csharp
namespace TPoolNet.Abstractions;

public interface IPooledTableLease : IAsyncDisposable
{
    int TablePoolId { get; }
    string SchemaName { get; }
    string TableName { get; }
    string FullQualifiedName { get; }
    string ConsumerId { get; }
    DateTime BookedAtUtc { get; }
    DateTime? DeadlineUtc { get; }
    bool IsPersistent { get; }
}

public interface ITablePoolService
{
    // Transient Booking
    Task<IPooledTableLease> BookAsync(
        string tableTypeName, 
        string consumerId, 
        CancellationToken cancellationToken = default);

    // Persistent Booking
    Task<IPooledTableLease> BookPersistentAsync(
        string tableTypeName, 
        string consumerId, 
        TimeSpan retentionPeriod, 
        CancellationToken cancellationToken = default);

    // Explicit Manual Release (Required for Persistent Leases)
    Task ReleaseAsync(
        string tableName, 
        string consumerId, 
        CancellationToken cancellationToken = default);

    // Heartbeat update for long-running transient tasks
    Task SendHeartbeatAsync(
        int tablePoolId, 
        CancellationToken cancellationToken = default);
}

public interface ITableProvisionerService
{
    Task ProvisionTablesAsync(string tableTypeName, int count, CancellationToken cancellationToken = default);
    Task DropTablesAsync(string tableTypeName, int count, bool force = false, CancellationToken cancellationToken = default);
    Task<int> EnsurePoolCapacityAsync(string tableTypeName, int targetCapacity, CancellationToken cancellationToken = default);
}
```

---

## 5. Repository Scaffolding Files

### Directory.Build.props

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AnalysisLevel>latest</AnalysisLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

### Directory.Packages.props

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- Core Dependencies -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="8.0.8"/>
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.8"/>
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.0"/>
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="8.0.0"/>
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.1"/>

    <!-- Test Dependencies -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
    <PackageVersion Include="xunit" Version="2.9.0"/>
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2"/>
    <PackageVersion Include="coverlet.collector" Version="6.0.2"/>
    <PackageVersion Include="Testcontainers.MsSql" Version="3.9.0"/>
    <PackageVersion Include="FluentAssertions" Version="6.12.0"/>
  </ItemGroup>
</Project>
```

---

## 6. Phased Implementation Plan

### Story 1: Project Scaffolding & EF Core Model Setup
**Goal**: Scaffold solution structure, Central Package Management, entity configurations, and TPoolDbContext.

**Tasks**:
- [ ] Create `TPoolNet.sln` with `src/TPoolNet/TPoolNet.csproj` and `tests/TPoolNet.Tests/TPoolNet.Tests.csproj`.
- [ ] Create `Directory.Build.props` and `Directory.Packages.props`.
- [ ] Implement domain entities (`TablesType`, `TablesConsumerType`, `TablesPool`, `TablesUsage`, `TablesUsageHistory`).
- [ ] Configure entity mapping in `TPoolDbContext` with fluent API schema validations.
- [ ] Add initial EF Core migration for schema creation.

**Acceptance Criteria**:
* `dotnet build` succeeds with zero warnings/errors.
* EF Core migration script successfully creates `tpool` schema and all 5 tables.

---

### Story 2: Atomic Table Leasing & RAII Disposable Pattern
**Goal**: Implement high-throughput, deadlock-free transient table leasing using `IAsyncDisposable`.

**Tasks**:
- [ ] Implement `ITablePoolService.BookAsync` using the atomic `UPDLOCK, READPAST` CTE query.
- [ ] Implement `PooledTableLease` with `IAsyncDisposable` support.
- [ ] Implement release logic: execute `TRUNCATE TABLE`, record history entry, and delete from `TablesUsage`.
- [ ] Expose service registration via `IServiceCollection.AddTPoolNet(...)`.

**Acceptance Criteria**:
* Booking returns an active table and blocks other threads from obtaining it concurrently.
* Disposing the lease triggers table truncation and returns the table to the pool.

---

### Story 3: Persistent Leasing & Explicit Release Support
**Goal**: Enable tables to survive consumer process boundaries with deadline tracking.

**Tasks**:
- [ ] Implement `ITablePoolService.BookPersistentAsync` setting `DeadlineUtc = UtcNow + retentionPeriod`.
- [ ] Ensure `PooledTableLease.DisposeAsync()` acts as a no-op when `IsPersistent == true`.
- [ ] Implement `ITablePoolService.ReleaseAsync(tableName, consumerId)` requiring matching consumer credentials.

**Acceptance Criteria**:
* Persistent leases retain all data when the booking process finishes and disposes the lease object.
* Another consumer/stage can access the table and explicitly release it using matching `consumerId`.

---

### Story 4: Dynamic Table Provisioner & DDL Engine
**Goal**: Programmatically provision physical pool tables according to `TablesType` templates, replacing legacy stored procedures.

**Tasks**:
- [ ] Implement `ITableProvisionerService.ProvisionTablesAsync`.
- [ ] Replace placeholders (`{SCHEMA}`, `{TABLENAME}`) in `DdlTemplate` and run `CREATE TABLE`.
- [ ] Enforce `MaxPoolSize` guardrails configured in `TablesType`.
- [ ] Implement `EnsurePoolCapacityAsync` to provision missing tables on startup.

**Acceptance Criteria**:
* Calling provisioner dynamically creates physical tables in SQL Server and adds matching entries in `TablesPool`.
* Cannot exceed `MaxPoolSize` configuration.

---

### Story 5: Heartbeat & Background Sweeper Service
**Goal**: Prevent resource leaks from crashed workers and expired persistent data holds.

**Tasks**:
- [ ] Implement `ITablePoolService.SendHeartbeatAsync` to update `HeartbeatUtc`.
- [ ] Create `TPoolZombieSweeperHostedService : BackgroundService`.
- [ ] Implement sweep query identifying zombie transient leases and expired persistent deadlines.
- [ ] Safely truncate, log history with reason (`SweptZombie` / `SweptExpired`), and remove expired leases.

**Acceptance Criteria**:
* Transient tables from ungracefully killed tasks are reclaimed after heartbeat expiration.
* Persistent tables exceeding `DeadlineUtc` are truncated and returned to pool.

---

### Story 6: Comprehensive Test Suite & Concurrency Verification
**Goal**: Validate data integrity, zero deadlocks, and high-concurrency safety using Testcontainers.

**Tasks**:
- [ ] Setup Testcontainers for SQL Server fixture in `TPoolNet.Tests`.
- [ ] Write concurrency test: Spin up 50 parallel tasks attempting to book from a pool of 10 tables.
- [ ] Write sweeper integration tests simulating abandoned leases and passed deadlines.
- [ ] Verify no deadlocks, zero double-allocations, and 100% lease reclamation under load.

**Acceptance Criteria**:
* All unit and integration tests pass cleanly in containerized environment.