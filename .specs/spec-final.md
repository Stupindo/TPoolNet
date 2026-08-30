# TPoolNet: Final Architecture Specification

> **Version**: 1.0 — 2026-08-30
> **Status**: Consolidated from spec-1, spec-2, and plan

---

## 1. System Overview & Core Objectives

**TPoolNet** is a high-performance .NET 8+ class library designed to eliminate SQL Server performance bottlenecks caused by frequent `#temp` table creation—specifically metadata catalog contention, DDL compilation locks, and `tempdb` allocation stalls.

The library replaces the legacy T-SQL stored-procedure-based TPool framework by moving all lifecycle, leasing, and dynamic table generation logic into C# and Entity Framework Core, while preserving only the necessary configuration and metadata tables in the database.

> [!IMPORTANT]
> Refer to the legacy project named **"TPool"** (`c:\Work\HomeCastle\TPool`) for specific domain logic and context when implementing. All legacy stored procedures and UDFs will be eliminated.

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

### 1.1 Key Functional Requirements

* **Legacy Stored Procedure & UDF Elimination**: The following legacy objects are replaced by compiled C# services and LINQ/raw SQL execution:
  - `sproc_BookTableFromTablesPool`
  - `sproc_ReleaseTablesPool`
  - `sproc_AddTablesPool`
  - `sproc_DropTablesPool`
  - `sproc_DropUnusedTablesPool`
  - `sproc_GenerateTableScript`
  - `sproc_GetCurrentTablesUsage`
  - `udf_GetPoolTableName`
  - `udf_GetTablesPoolByConsumer`
  - `udf_GetTablesTypeId`

* **Two Distinct Leasing Modes**:
  1. *Transient Mode*: Short-lived lease implementing `IAsyncDisposable`. Scope exit automatically truncates the physical table and returns the slot to the pool.
  2. *Persistent Mode*: Multi-stage or cross-process lease. The table data survives consumer application termination. The table remains held until explicit release via API or until a configured `DeadlineUtc` is reached.

* **Atomic Concurrency Engine**: Safe multi-threaded table reservation using SQL Server locking hints (`UPDLOCK, ROWLOCK, READPAST`) to prevent deadlocks and race conditions under high concurrent demand.

* **Background Zombie & Deadline Sweeper**: Hosted background service (`IHostedService`) monitoring heartbeats for crashed transient workers and deadlines for expired persistent stages.

### 1.2 Legacy Schema Divergences (Intentional)

The following structural changes from the legacy TPool schema are intentional design decisions:

| Aspect | Legacy TPool | TPoolNet | Rationale |
|---|---|---|---|
| `TablesPool` columns | Only ID, TypeID, CreatedDate | Adds `SchemaName`, `TableName`, `IsActive` | Explicit storage vs. computed derivation |
| `TablesUsage` PK | Own `TablesUsageID bigint IDENTITY` | Uses `TablePoolId` as PK (1:1 with TablesPool) | Simplifies to a true 1:1 relationship |
| `TablesUsage.ConsumerId` | Two columns: `TablesConsumerTypeID` + `TablesConsumerID` | Single `ConsumerId VARCHAR(100)` | Flattened composite key into a string identifier |
| Heartbeat/Deadline | Not present in legacy | New `HeartbeatUtc`, `DeadlineUtc` | Enables zombie detection and persistent deadline tracking |
| `TablesType` templates | 3 columns: `GenerateTableScript`, `ClusteredIndexScriptTemplate`, `NonclusteredIndexScriptTemplate` | Single `DdlTemplate` | Consolidated template management |
| `TablesType.TypeName` length | `nvarchar(257)` | `VARCHAR(50)` | Tightened to practical limits |

---

## 2. Repository Structure & Configuration

The GitHub repository is named **TPoolNet**. The solution uses .NET 8 best practices.

```text
TPoolNet/
├── TPoolNet.sln
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── src/
│   └── TPoolNet/
│       ├── TPoolNet.csproj
│       ├── Abstractions/
│       │   ├── IPooledTableLease.cs
│       │   ├── ITablePoolService.cs
│       │   └── ITableProvisionerService.cs
│       ├── Data/
│       │   └── TPoolDbContext.cs
│       ├── Entities/
│       │   ├── TablesType.cs
│       │   ├── TablesConsumerType.cs
│       │   ├── TablesPool.cs
│       │   ├── TablesUsage.cs
│       │   └── TablesUsageHistory.cs
│       ├── Services/
│       │   ├── TablePoolService.cs
│       │   ├── TableProvisionerService.cs
│       │   ├── PooledTableLease.cs
│       │   └── TPoolZombieSweeperHostedService.cs
│       └── Extensions/
│           └── ServiceCollectionExtensions.cs
└── tests/
    └── TPoolNet.Tests/
        └── TPoolNet.Tests.csproj
```

### 2.1 Directory.Build.props

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

### 2.2 Directory.Packages.props

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

## 3. Database Schema & EF Core Model

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
│ PK  TablePoolId     BIGINT      │
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
│ PK  TablePoolId     BIGINT      │
│     ConsumerId      VARCHAR(100)│
│     BookedAtUtc     DATETIME2   │
│     HeartbeatUtc    DATETIME2   │
│     DeadlineUtc     DATETIME2?  │ <── NULL = Transient; Non-NULL = Persistent
└─────────────────────────────────┘

┌─────────────────────────────────┐
│  [tpool].[TablesConsumerType]   │
├─────────────────────────────────┤
│ PK  ConsumerTypeId  INT         │
│     ConsumerName    VARCHAR(100)│
│     Description     VARCHAR(500)│ <── Optional
└─────────────────────────────────┘

┌─────────────────────────────────┐
│  [tpool].[TablesUsageHistory]   │
├─────────────────────────────────┤
│ PK  HistoryId       BIGINT      │
│     TablePoolId     BIGINT      │
│     ConsumerId      VARCHAR(100)│
│     BookedAtUtc     DATETIME2   │
│     ReleasedAtUtc   DATETIME2   │
│     ReleaseReason   VARCHAR(50) │ <── "Explicit"|"Disposed"|"SweptZombie"|"SweptExpired"
└─────────────────────────────────┘
```

### 3.1 Entity Classes

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
    public long TablePoolId { get; set; }
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
    public long TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public DateTime? DeadlineUtc { get; set; }

    public virtual TablesPool TablePool { get; set; } = null!;
}

public class TablesUsageHistory
{
    public long HistoryId { get; set; }
    public long TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime ReleasedAtUtc { get; set; }
    public string ReleaseReason { get; set; } = "Explicit"; // "Explicit", "Disposed", "SweptZombie", "SweptExpired"
}
```

### 3.2 Fluent Configuration (TPoolDbContext)

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
            b.Property(e => e.Description).HasMaxLength(500);
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
            b.HasIndex(e => e.BookedAtUtc);
        });
    }
}
```

---

## 4. Core Mechanics & Algorithms

### 4.1 Atomic Booking Query

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

### 4.2 Dynamic Table Provisioner

Replaces legacy `sproc_AddTablesPool` and `sproc_GenerateTableScript`:

* Fetches the `DdlTemplate` from `TablesType` (e.g., `CREATE TABLE [{SCHEMA}].[{TABLENAME}] (Id INT IDENTITY, Payload NVARCHAR(MAX), CreatedAt DATETIME2)`).
* Calculates next sequence index for naming: `tbl_{TablePrefix}_{Sequence:D3}`.
* Generates and executes DDL via `Database.ExecuteSqlRawAsync(...)`.
* Registers the newly created physical table into `[tpool].[TablesPool]` inside a local transaction.
* Enforces `MaxPoolSize` guardrails configured in `TablesType`.

### 4.3 Lease Lifecycle Engine (Transient vs. Persistent)

```csharp
namespace TPoolNet.Services;

using TPoolNet.Abstractions;

public class PooledTableLease : IPooledTableLease
{
    private readonly Func<PooledTableLease, string, Task> _releaseAction;
    private bool _isDisposed;

    public long TablePoolId { get; init; }
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

### 4.4 Sweeper Background Engine

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
1. `TRUNCATE TABLE [{SchemaName}].[{TableName}]`
2. `INSERT INTO [tpool].[TablesUsageHistory]` logging duration, ConsumerId, and `CleanupReason`.
3. `DELETE FROM [tpool].[TablesUsage] WHERE TablePoolId = @TablePoolId`.

---

## 5. Public API & Interfaces

```csharp
namespace TPoolNet.Abstractions;

public interface IPooledTableLease : IAsyncDisposable
{
    long TablePoolId { get; }
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
        long tablePoolId, 
        CancellationToken cancellationToken = default);
}

public interface ITableProvisionerService
{
    Task ProvisionTablesAsync(
        string tableTypeName, 
        int count, 
        CancellationToken cancellationToken = default);

    Task DropTablesAsync(
        string tableTypeName, 
        int count, 
        bool force = false, 
        CancellationToken cancellationToken = default);

    Task<int> EnsurePoolCapacityAsync(
        string tableTypeName, 
        int targetCapacity, 
        CancellationToken cancellationToken = default);
}
```

### 5.1 DI Registration

```csharp
// Usage
services.AddTPoolNet(options =>
{
    options.ConnectionString = "...";
    options.HeartbeatTimeoutSeconds = 120;
    options.SweeperIntervalSeconds = 60;
});
```
