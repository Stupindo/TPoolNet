# Story 1 Implementation Report: Project Scaffolding & EF Core Model Setup

> **Date**: 2026-08-30
> **Branch**: `main`
> **Status**: ✅ Complete — all acceptance criteria met

---

## Acceptance Criteria Verification

| Criterion | Result |
|---|---|
| `dotnet build` succeeds with **zero warnings / zero errors** | ✅ `Build succeeded. 0 Warning(s) 0 Error(s)` |
| EF Core migration script creates `[tpool]` schema and all 5 tables with correct column types (notably `BIGINT` for `TablePoolId`) | ✅ Confirmed in generated SQL — see [migration-script.sql](file:///c:/Work/HomeCastle/TPoolNet/.specs/migration-script.sql) |

---

## Tasks Completed

### ✅ Solution & Project Structure
The solution structure already existed (`TPoolNet.sln`, `src/TPoolNet`, `tests/TPoolNet.Tests`). No scaffolding changes needed.

### ✅ Directory.Build.props
**File**: [Directory.Build.props](file:///c:/Work/HomeCastle/TPoolNet/Directory.Build.props)

Added `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` as required by spec §2.1. The property was absent from the initial file.

### ✅ Directory.Packages.props
**File**: [Directory.Packages.props](file:///c:/Work/HomeCastle/TPoolNet/Directory.Packages.props)

Added missing packages from spec §2.2:
- `Microsoft.EntityFrameworkCore.Relational` 8.0.13
- `Microsoft.EntityFrameworkCore.Design` 8.0.13 *(design-time only)*
- `Microsoft.Extensions.Hosting.Abstractions` 8.0.1
- `Microsoft.Extensions.Options.ConfigurationExtensions` 8.0.0
- `Microsoft.Extensions.Logging.Abstractions` 8.0.2
- `Testcontainers.MsSql` 3.10.0 *(for Story 6)*
- `FluentAssertions` 6.12.0 *(for Story 6)*

### ✅ NuGet.Config — Package Source Mapping
**File**: [NuGet.Config](file:///c:/Work/HomeCastle/TPoolNet/NuGet.Config) *(new)*

The repo has two NuGet sources configured (`nuget.org` + `github` private feed). Central Package Management requires explicit **Package Source Mapping** when multiple sources exist (NU1507). A `NuGet.Config` was created at the repo root routing all TPoolNet packages to `nuget.org` and Stratly private packages to `github`.

### ✅ Domain Entities (5 classes)

All created in `src/TPoolNet/Entities/`:

| File | Entity | PK Type | Notes |
|---|---|---|---|
| [TablesType.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesType.cs) | `TablesType` | `INT` | Includes `DdlTemplate`, `TablePrefix`, `MaxPoolSize`, navigation to `TablesPool` |
| [TablesConsumerType.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesConsumerType.cs) | `TablesConsumerType` | `INT` | Reference table; `Description` is nullable |
| [TablesPool.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesPool.cs) | `TablesPool` | `BIGINT` | `SchemaName` defaults to `"tpool"`; `IsActive`, `CreatedAtUtc`; nav to `TablesType` and `ActiveUsage` |
| [TablesUsage.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesUsage.cs) | `TablesUsage` | `BIGINT` (shared with `TablesPool`) | `DeadlineUtc` nullable — NULL=Transient, Non-NULL=Persistent |
| [TablesUsageHistory.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesUsageHistory.cs) | `TablesUsageHistory` | `BIGINT` IDENTITY | `ReleaseReason` defaults to `"Explicit"`; 4 valid reasons documented |

### ✅ TPoolDbContext with Fluent API
**File**: [TPoolDbContext.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Data/TPoolDbContext.cs)

Implemented per spec §3.2 with all required configurations:

- Default schema: `"tpool"`
- All `VARCHAR`/`SYSNAME`/`NVARCHAR(MAX)` column types explicitly set
- `HasPrecision(2)` on all `DATETIME2` columns
- **Unique indexes**: `TypeName` on `TablesType`, `(SchemaName, TableName)` on `TablesPool`
- **Relationships**:
  - `TablesType` → `TablesPool` (1:many, `OnDelete: Restrict`)
  - `TablesPool` → `TablesUsage` (1:0..1, `OnDelete: Cascade`)
- **Shared PK**: `TablesUsage.TablePoolId` configured as `ValueGeneratedNever()` to enforce the 1:1 with `TablesPool`
- **Audit indexes**: `TablesUsageHistory.TablePoolId` and `.BookedAtUtc`
- CA1062 null-guard: `ArgumentNullException.ThrowIfNull(modelBuilder)`

### ✅ Design-Time Factory
**File**: [TPoolDbContextFactory.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Data/TPoolDbContextFactory.cs) *(new)*

`IDesignTimeDbContextFactory<TPoolDbContext>` implementation (`internal sealed`) enabling `dotnet-ef` tooling to create the context without a running host. Also configures `MigrationsHistoryTable` to `[tpool].[__EFMigrationsHistory]` (keeping everything in the `tpool` schema).

### ✅ Initial EF Core Migration
**Migration**: `InitialTPoolSchema` (20260830162151)

**File**: [20260830162151_InitialTPoolSchema.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Migrations/20260830162151_InitialTPoolSchema.cs)

Verified SQL output creates:
- `[tpool]` schema via `EnsureSchema`
- ✅ `TablesConsumerType` (INT PK)
- ✅ `TablesType` (INT PK, `varchar(50)` TypeName, `nvarchar(max)` DdlTemplate)
- ✅ `TablesUsageHistory` (BIGINT IDENTITY PK)
- ✅ `TablesPool` (BIGINT IDENTITY PK, SYSNAME columns, FK→TablesType RESTRICT)
- ✅ `TablesUsage` (BIGINT PK shared with TablesPool, FK→TablesPool CASCADE)

Idempotent SQL script: [.specs/migration-script.sql](file:///c:/Work/HomeCastle/TPoolNet/.specs/migration-script.sql)

---

## Issues Encountered & Resolutions

| Issue | Resolution |
|---|---|
| **NU1507**: Multiple NuGet sources + CPM requires Package Source Mapping | Created `NuGet.Config` at repo root with explicit `<packageSourceMapping>` |
| **CA1062** on `OnModelCreating(ModelBuilder)` | Added `ArgumentNullException.ThrowIfNull(modelBuilder)` |
| **CA1062 + CA1861** on generated migration file | Suppressed via `.editorconfig`: `generated_code = true` + `dotnet_analyzer_diagnostic.severity = none` for `Migrations/**` |
| **`dotnet-ef` not installed globally** | `dotnet tool install --global dotnet-ef` (v10.0.11 installed) |
| **Design-time context creation failure** | Added `TPoolDbContextFactory : IDesignTimeDbContextFactory<TPoolDbContext>` |

---

## Files Created / Modified

### New Files
| Path | Description |
|---|---|
| [NuGet.Config](file:///c:/Work/HomeCastle/TPoolNet/NuGet.Config) | Package source mapping for multi-source CPM |
| [Entities/TablesType.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesType.cs) | Domain entity |
| [Entities/TablesConsumerType.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesConsumerType.cs) | Domain entity |
| [Entities/TablesPool.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesPool.cs) | Domain entity |
| [Entities/TablesUsage.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesUsage.cs) | Domain entity |
| [Entities/TablesUsageHistory.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Entities/TablesUsageHistory.cs) | Domain entity |
| [Data/TPoolDbContext.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Data/TPoolDbContext.cs) | EF Core DbContext with full fluent configuration |
| [Data/TPoolDbContextFactory.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Data/TPoolDbContextFactory.cs) | Design-time factory for `dotnet-ef` tooling |
| [Migrations/20260830162151_InitialTPoolSchema.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Migrations/20260830162151_InitialTPoolSchema.cs) | EF Core migration |
| [Migrations/TPoolDbContextModelSnapshot.cs](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/Migrations/TPoolDbContextModelSnapshot.cs) | EF Core model snapshot |

### Deleted Files
| Path | Reason |
|---|---|
| `src/TPoolNet/Class1.cs` | Placeholder stub from project template |

### Modified Files
| Path | Change |
|---|---|
| [Directory.Build.props](file:///c:/Work/HomeCastle/TPoolNet/Directory.Build.props) | Added `TreatWarningsAsErrors=true` |
| [Directory.Packages.props](file:///c:/Work/HomeCastle/TPoolNet/Directory.Packages.props) | Added 7 missing package versions |
| [src/TPoolNet/TPoolNet.csproj](file:///c:/Work/HomeCastle/TPoolNet/src/TPoolNet/TPoolNet.csproj) | Added 5 package references + EF Design (PrivateAssets=all) |
| [.editorconfig](file:///c:/Work/HomeCastle/TPoolNet/.editorconfig) | Added Migrations folder suppression section |

---

## Ready for Story 2
Story 1 delivers a clean, fully-configured EF Core model with a passing `dotnet build` and a working migration. The next step is **Story 2: Atomic Table Leasing & RAII Disposable Pattern** — implementing `IPooledTableLease`, `ITablePoolService`, `PooledTableLease`, and the CTE booking query.
