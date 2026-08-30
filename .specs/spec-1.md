# TPoolNet Implementation Specification

## Context & Architecture
TPoolNet is a .NET 8+ class library replacing a legacy T-SQL framework (TPool) to eliminate performance bottlenecks and DDL overhead. 
*   Initialize targeting `net8.0` with `Directory.Packages.props` for Central Package Management[cite: 2].
*   Structure includes `src/TPoolNet/` for the core library and `tests/TPoolNet.Tests/` for testing[cite: 2].
*   Use Entity Framework Core for schema management and raw SQL for atomic locking[cite: 2].

## Database Schema & EF Core Model
Create `TPoolDbContext` using the Fluent API to provision the `[tpool]` schema and metadata[cite: 1].
*   **TablesType / TablesConsumerType**: Configuration for generation templates and consumer categories[cite: 1].
*   **TablesPool**: Registry of physical staging tables[cite: 1].
*   **TablesUsage**: Active lease tracking, featuring `HeartbeatUtc` (transient) and `DeadlineUtc` (persistent)[cite: 1].
*   **TablesUsageHistory**: Telemetry log for completed or swept leases[cite: 1].

## Core API & Booking Engine
Implement `ITablePoolService` to manage table leases atomically using CTEs and SQL Server locking hints (`UPDLOCK`, `ROWLOCK`, `READPAST`).
*   **Transient Leases**: `BookAsync` returns an `IAsyncDisposable` lease that truncates the table and releases the record on disposal.
*   **Persistent Leases**: `BookPersistentAsync` returns a lease that skips disposal truncation, requiring explicit cleanup via `ReleaseByNameAsync`.
*   **Dynamic Provisioning**: `ProvisionTablesAsync` formats the `TablesType` DDL template and executes `CREATE TABLE` to expand pool capacity dynamically[cite: 1].

## Background Maintenance & Testing
Ensure reliability through background sweeping and robust integration testing.
*   Implement an `IHostedService` to sweep abandoned tables (expired heartbeats) and expired persistent leases (passed deadlines).
*   Use Testcontainers for SQL Server to execute integration tests against an isolated, containerized database instance.
*   Create xUnit tests simulating 50+ concurrent workers to verify zero deadlocks and exact booking counts[cite: 2].