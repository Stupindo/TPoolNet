# TPoolNet: Final Implementation Plan

> **Version**: 1.0 — 2026-08-30
> **Companion**: [spec-final.md](file:///c:/Work/HomeCastle/TPoolNet/.specs/spec-final.md)

---

## Story 1: Project Scaffolding & EF Core Model Setup

**Goal**: Scaffold solution structure, Central Package Management, entity configurations, and TPoolDbContext.

**Tasks**:
- [x] Create `TPoolNet.sln` with `src/TPoolNet/TPoolNet.csproj` and `tests/TPoolNet.Tests/TPoolNet.Tests.csproj`.
- [x] Create `Directory.Build.props` and `Directory.Packages.props` with all packages listed in spec §2.1–2.2.
- [x] Implement domain entities in `TPoolNet.Entities` namespace:
  - `TablesType` (PK: `int TableTypeId`)
  - `TablesConsumerType` (PK: `int ConsumerTypeId`, includes `Description`)
  - `TablesPool` (PK: `long TablePoolId`, includes `SchemaName`, `TableName`, `IsActive`, `CreatedAtUtc`)
  - `TablesUsage` (PK: `long TablePoolId`, 1:1 with TablesPool, includes `HeartbeatUtc`, `DeadlineUtc`)
  - `TablesUsageHistory` (PK: `long HistoryId`, `ReleaseReason` defaults to `"Explicit"`)
- [x] Configure entity mapping in `TPoolDbContext` with fluent API per spec §3.2:
  - Default schema `"tpool"`
  - All property constraints (max lengths, precision, required)
  - Unique indexes (`TypeName`, `SchemaName+TableName`)
  - Relationships (`TablesType` → `TablesPool` 1:*, `TablesPool` → `TablesUsage` 1:0..1)
  - Delete behaviors (`Restrict` on Type→Pool, `Cascade` on Pool→Usage)
- [x] Add initial EF Core migration for schema creation.

**Acceptance Criteria**:
* `dotnet build` succeeds with zero warnings/errors.
* EF Core migration script successfully creates `[tpool]` schema and all 5 tables with correct column types (notably `BIGINT` for `TablePoolId`).

---

## Story 2: Atomic Table Leasing & RAII Disposable Pattern

**Goal**: Implement high-throughput, deadlock-free transient table leasing using `IAsyncDisposable`.

**Tasks**:
- [x] Create `IPooledTableLease` interface in `TPoolNet.Abstractions` namespace exposing:
  - `long TablePoolId`, `string SchemaName`, `string TableName`, `string FullQualifiedName`
  - `string ConsumerId`, `DateTime BookedAtUtc`, `DateTime? DeadlineUtc`, `bool IsPersistent`
- [x] Create `ITablePoolService` interface with `BookAsync`, `BookPersistentAsync`, `ReleaseAsync`, `SendHeartbeatAsync`.
- [x] Implement `PooledTableLease` class in `TPoolNet.Services`:
  - Constructor accepts `Func<PooledTableLease, string, Task>` release callback.
  - `IsPersistent => DeadlineUtc.HasValue`.
  - `DisposeAsync()` only triggers release for transient leases (reason: `"Disposed"`).
- [x] Implement `TablePoolService.BookAsync` using the atomic `UPDLOCK, READPAST` CTE query from spec §4.1.
- [x] Implement release logic in `TablePoolService`:
  1. Execute `TRUNCATE TABLE [{SchemaName}].[{TableName}]`.
  2. Insert record into `TablesUsageHistory` with release reason.
  3. Delete from `TablesUsage`.
- [x] Create `ServiceCollectionExtensions.AddTPoolNet(...)` for DI registration.

**Acceptance Criteria**:
* Booking returns an active lease and blocks other threads from obtaining the same table concurrently.
* Disposing a transient lease triggers table truncation and returns the table to the pool.

---

## Story 3: Persistent Leasing & Explicit Release Support

**Goal**: Enable tables to survive consumer process boundaries with deadline tracking.

**Tasks**:
- [x] Implement `TablePoolService.BookPersistentAsync`:
  - Sets `DeadlineUtc = SYSUTCDATETIME() + retentionPeriod` in the CTE query's `@DeadlineUtc` parameter.
- [x] Verify `PooledTableLease.DisposeAsync()` acts as a no-op when `IsPersistent == true` (no truncation, no release).
- [x] Implement `TablePoolService.ReleaseAsync(tableName, consumerId)`:
  - Validates matching `ConsumerId` before releasing.
  - Executes truncate → history insert → usage delete.

**Acceptance Criteria**:
* Persistent leases retain all data when the booking process finishes and disposes the lease object.
* Another consumer/stage can access the table by its name and explicitly release it using matching `consumerId`.
* Attempting to release with a mismatched `consumerId` throws or returns an error.

---

## Story 4: Dynamic Table Provisioner & DDL Engine

**Goal**: Programmatically provision physical pool tables according to `TablesType` templates, replacing legacy stored procedures `sproc_AddTablesPool` and `sproc_GenerateTableScript`.

**Tasks**:
- [ ] Create `ITableProvisionerService` public interface in `TPoolNet.Abstractions` with:
  - `ProvisionTablesAsync(tableTypeName, count, ct)`
  - `DropTablesAsync(tableTypeName, count, force, ct)`
  - `EnsurePoolCapacityAsync(tableTypeName, targetCapacity, ct)`
- [ ] Implement `TableProvisionerService.ProvisionTablesAsync`:
  - Fetch `DdlTemplate` from `TablesType` by name.
  - Calculate next sequence index: `tbl_{TablePrefix}_{Sequence:D3}`.
  - Replace placeholders `{SCHEMA}` and `{TABLENAME}` in template.
  - Execute `CREATE TABLE` via `Database.ExecuteSqlRawAsync(...)`.
  - Register new table in `[tpool].[TablesPool]` within a local transaction.
- [ ] Enforce `MaxPoolSize` guardrails — reject provisioning beyond configured limit.
- [ ] Implement `DropTablesAsync` — drop unused (not booked) tables, optionally `force` to drop even active ones.
- [ ] Implement `EnsurePoolCapacityAsync` — provision missing tables up to `targetCapacity` on startup.

**Acceptance Criteria**:
* Calling provisioner dynamically creates physical tables in SQL Server and adds matching entries in `TablesPool`.
* Cannot exceed `MaxPoolSize` configuration; operation throws if limit reached.
* `EnsurePoolCapacityAsync` is idempotent — calling it when capacity is already met provisions zero tables.

---

## Story 5: Heartbeat & Background Sweeper Service

**Goal**: Prevent resource leaks from crashed workers and expired persistent data holds.

**Tasks**:
- [ ] Implement `TablePoolService.SendHeartbeatAsync(long tablePoolId)`:
  - Updates `HeartbeatUtc = SYSUTCDATETIME()` on the matching `TablesUsage` row.
- [ ] Create `TPoolZombieSweeperHostedService : BackgroundService`:
  - Runs on a configurable interval (`SweeperIntervalSeconds` from options).
  - Uses `ILogger<TPoolZombieSweeperHostedService>` for structured logging.
- [ ] Implement sweep query from spec §4.4 identifying:
  - Zombie transient leases: `DeadlineUtc IS NULL AND HeartbeatUtc < (now - HeartbeatTimeoutSeconds)`.
  - Expired persistent leases: `DeadlineUtc IS NOT NULL AND DeadlineUtc < now`.
- [ ] For each identified record, safely execute:
  1. `TRUNCATE TABLE [{SchemaName}].[{TableName}]`
  2. `INSERT INTO [tpool].[TablesUsageHistory]` with `ConsumerId`, `BookedAtUtc`, and reason (`SweptZombie` / `SweptExpired`).
  3. `DELETE FROM [tpool].[TablesUsage] WHERE TablePoolId = @TablePoolId`.
- [ ] Ensure sweeper uses `UPDLOCK, READPAST` hints to avoid blocking the booking engine.

**Acceptance Criteria**:
* Transient tables from ungracefully killed tasks are reclaimed after heartbeat timeout expiration.
* Persistent tables exceeding `DeadlineUtc` are truncated and returned to pool.
* Sweeper logs each cleanup action with structured logging (table name, consumer, reason).

---

## Story 6: Comprehensive Test Suite & Concurrency Verification

**Goal**: Validate data integrity, zero deadlocks, and high-concurrency safety using Testcontainers.

**Tasks**:
- [ ] Setup Testcontainers for SQL Server fixture in `TPoolNet.Tests`:
  - Shared container instance across test classes via `IAsyncLifetime`.
  - Auto-run EF Core migrations against containerized SQL Server.
- [ ] Write unit tests for `PooledTableLease`:
  - Verify transient `DisposeAsync` triggers release callback.
  - Verify persistent `DisposeAsync` is a no-op.
- [ ] Write integration tests for booking lifecycle:
  - Book → verify usage row exists → dispose → verify usage row deleted + history row created.
  - Book persistent → dispose → verify usage row still exists → explicit release → verify cleanup.
- [ ] Write concurrency test:
  - Spin up 50 parallel tasks attempting to book from a pool of 10 tables.
  - Assert exactly 10 bookings succeed and 40 fail (or wait/retry depending on design).
  - Assert zero deadlocks and zero double-allocations.
- [ ] Write sweeper integration tests:
  - Simulate abandoned transient lease (stale heartbeat) → run sweeper → verify cleanup.
  - Simulate expired persistent lease (past deadline) → run sweeper → verify cleanup.
  - Verify `TablesUsageHistory` records have correct `ReleaseReason`.
- [ ] Write provisioner integration tests:
  - Provision tables → verify physical tables exist in SQL Server.
  - Attempt to exceed `MaxPoolSize` → verify rejection.
  - `EnsurePoolCapacityAsync` idempotency test.
- [ ] Verify 100% lease reclamation under load — no leaked tables after all tests complete.

**Acceptance Criteria**:
* All unit and integration tests pass cleanly in containerized environment.
* Zero deadlocks observed under concurrent load.
* Zero double-allocations — each table is booked by at most one consumer at a time.
* `dotnet test` exits with code 0 and all assertions pass.
