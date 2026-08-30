# TPoolNet: Architecture Specification & Phased Implementation Plan

## 1. System Overview & Core Objectives

**TPoolNet** is a high-performance .NET 8 class library targeting SQL Server performance issues. It eliminates the metadata catalog contention, DDL compilation locks, and `tempdb` allocation stalls typically caused by frequent `#temp` table creation.

The library replaces the legacy T-SQL stored-procedure-based framework by moving lifecycle, leasing, and dynamic table generation logic into C# using Entity Framework Core. Ensure you refer to the legacy file named "TPool" for specific domain logic and context when implementing.

### Key Functional Requirements

- **Two Distinct Leasing Modes**:
    1. _Transient Mode_: Short-lived lease implementing `IAsyncDisposable`. Scope exit automatically truncates the physical table and returns the slot to the pool.
    2. _Persistent Mode_: Multi-stage or cross-process lease. The table data survives consumer application termination. The table remains held until explicit release via API or until a configured `DeadlineUtc` is reached.
- **Atomic Concurrency Engine**: Safe multi-threaded table reservation using SQL Server locking hints (`UPDLOCK, ROWLOCK, READPAST`) to prevent deadlocks.
- **Background Sweeper**: Hosted background service (`IHostedService`) monitoring heartbeats for crashed transient workers and deadlines for expired persistent stages.

---

## 2. Repository Structure & Configuration

The GitHub repository is named TPoolNet. The solution must be structured using the best practices for .NET 8 libraries.

- **`src/TPoolNet/`**: The core .NET 8 Class Library containing the EF Core implementation[cite: 2].
- **`tests/TPoolNet.Tests/`**: A dedicated xUnit test project referencing the `src` project[cite: 2].

### Shared Properties (`Directory.Build.props`)

To keep the `.csproj` files clean, repository-wide properties must be set here[cite: 2]:

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

### Central Package Management (Directory.Packages.props)

The solution must use Central Package Management to synchronize dependencies across the src and tests folders[cite: 2]:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="8.0.8"/>
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.0"/>
    <PackageVersion Include="xunit" Version="2.9.0"/>
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2"/>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
    <PackageVersion Include="coverlet.collector" Version="6.0.2"/>
    <PackageVersion Include="Testcontainers.MsSql" Version="3.9.0"/>
  </ItemGroup>
</Project>
```

## 3. Database Schema & EF Core Model

All metadata tables reside in the dedicated [tpool] database schema.

### EF Core Entity Classes

```
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
}

public class TablesPool
{
    public int TablePoolId { get; set; }
    public int TableTypeId { get; set; }
    public string SchemaName { get; set; } = "tpool";
    public string TableName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public virtual TablesType TableType { get; set; } = null!;
    public virtual TablesUsage? ActiveUsage { get; set; }
}

public class TablesUsage
{
    public int TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public DateTime? DeadlineUtc { get; set; } // NULL = Transient; Non-NULL = Persistent
    public virtual TablesPool TablePool { get; set; } = null!;
}

public class TablesUsageHistory
{
    public long HistoryId { get; set; }
    public int TablePoolId { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public DateTime BookedAtUtc { get; set; }
    public DateTime ReleasedAtUtc { get; set; }
    public string ReleaseReason { get; set; } = string.Empty;
}
```

## 4. Core Mechanics & Algorithms

### 4.1 Atomic Booking Query

Table reservation uses a Common Table Expression (CTE) executed with locking hints in a single atomic SQL roundtrip:

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
OUTPUT inserted.TablePoolId, inserted.ConsumerId, inserted.BookedAtUtc, inserted.DeadlineUtc,
       (SELECT SchemaName FROM AvailableTable) AS SchemaName,
       (SELECT TableName FROM AvailableTable) AS TableName
SELECT TablePoolId, @ConsumerId, SYSUTCDATETIME(), SYSUTCDATETIME(), @DeadlineUtc
FROM AvailableTable;
```

### 4.2 Dynamic Table Provisioner

Fetches the DdlTemplate from TablesType.

Calculates next sequence index for naming: tbl*{TablePrefix}*{Sequence:D3}.

Generates and executes DDL via DbContext.Database.ExecuteSqlRawAsync(...).

Registers the newly created physical table into [tpool].[TablesPool].

### 4.3 Sweeper Background Engine

The TPoolZombieSweeperHostedService periodically executes a cleanup query for expired heartbeats/deadlines:

```sql
SELECT u.TablePoolId, p.SchemaName, p.TableName,
       CASE
           WHEN u.DeadlineUtc IS NOT NULL AND u.DeadlineUtc < SYSUTCDATETIME() THEN 'SweptExpired'
           ELSE 'SweptZombie'
       END AS CleanupReason
FROM [tpool].[TablesUsage] u WITH (UPDLOCK, READPAST)
JOIN [tpool].[TablesPool] p ON u.TablePoolId = p.TablePoolId
WHERE (u.DeadlineUtc IS NOT NULL AND u.DeadlineUtc < SYSUTCDATETIME())
   OR (u.DeadlineUtc IS NULL AND u.HeartbeatUtc < DATEADD(second, -@Timeout, SYSUTCDATETIME()));
```

## 5. Public API & Interfaces

```
namespace TPoolNet.Abstractions;

public interface IPooledTableLease : IAsyncDisposable
{
    int TablePoolId { get; }
    string SchemaName { get; }
    string TableName { get; }
    string FullQualifiedName { get; }
    bool IsPersistent { get; }
}

public interface ITablePoolService
{
    // Transient Booking
    Task<IPooledTableLease> BookAsync(string tableTypeName, string consumerId, CancellationToken ct = default);

    // Persistent Booking
    Task<IPooledTableLease> BookPersistentAsync(string tableTypeName, string consumerId, TimeSpan retentionPeriod, CancellationToken ct = default);

    // Explicit Manual Release (Required for Persistent Leases)
    Task ReleaseAsync(string tableName, string consumerId, CancellationToken ct = default);

    // Heartbeat update for long-running transient tasks
    Task SendHeartbeatAsync(int tablePoolId, CancellationToken ct = default);
}
```

## 6. Phased Implementation Plan

### Story 1: Project Scaffolding & EF Core Model Setup

Goal: Scaffold solution structure and implement the base entity mapping.

Tasks:

Create TPoolNet solution with src/TPoolNet and tests/TPoolNet.Tests[cite: 2].

Implement Directory.Build.props and Directory.Packages.props[cite: 2].

Implement EF Core domain entities and TPoolDbContext configuration.

Review the file named "TPool" to ensure all necessary schema definitions align[cite: 2].

Create the initial EF Core Migration.

### Story 2: Atomic Table Leasing & RAII Disposable Pattern

Goal: Implement deadlock-free transient table leasing.

Tasks:

Implement ITablePoolService.BookAsync using the atomic UPDLOCK, READPAST CTE query.

Implement PooledTableLease with IAsyncDisposable support.

Expose service registration via IServiceCollection.AddTPoolNet(...).

Story 3: Persistent Leasing & Explicit Release Support
Goal: Enable tables to survive consumer process boundaries.

Tasks:

Implement ITablePoolService.BookPersistentAsync mapping to DeadlineUtc.

Ensure PooledTableLease.DisposeAsync() bypasses table truncation when IsPersistent == true.

Implement ITablePoolService.ReleaseAsync(tableName, consumerId).

### Story 4: Dynamic Table Provisioner & DDL Engine

Goal: Programmatically provision physical pool tables.

Tasks:

Implement ITableProvisionerService.ProvisionTablesAsync.

Replace placeholders ({SCHEMA}, {TABLENAME}) in DdlTemplate and run CREATE TABLE.

Enforce MaxPoolSize configurations dynamically.

### Story 5: Background Sweeper Service

Goal: Prevent resource leaks from crashed workers or abandoned data limits.

Tasks:

Implement ITablePoolService.SendHeartbeatAsync.

Create TPoolZombieSweeperHostedService executing the sweeping query.

Safely truncate tables and log history (SweptZombie / SweptExpired).

### Story 6: Comprehensive Test Suite

Goal: Validate data integrity and verify zero deadlocks.

Tasks:

Setup Testcontainers for SQL Server fixture in the xUnit test project[cite: 2].

Write high-concurrency simulation test: Spin up 50 parallel tasks attempting to book from a pool of 10 tables.

Write sweeper integration tests simulating abandoned leases and passed deadlines.
