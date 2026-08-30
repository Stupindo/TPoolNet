IF OBJECT_ID(N'[tpool].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'tpool') IS NULL EXEC(N'CREATE SCHEMA [tpool];');
    CREATE TABLE [tpool].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    IF SCHEMA_ID(N'tpool') IS NULL EXEC(N'CREATE SCHEMA [tpool];');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE TABLE [tpool].[TablesConsumerType] (
        [ConsumerTypeId] int NOT NULL IDENTITY,
        [ConsumerName] varchar(100) NOT NULL,
        [Description] varchar(500) NULL,
        CONSTRAINT [PK_TablesConsumerType] PRIMARY KEY ([ConsumerTypeId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE TABLE [tpool].[TablesType] (
        [TableTypeId] int NOT NULL IDENTITY,
        [TypeName] varchar(50) NOT NULL,
        [TablePrefix] varchar(50) NOT NULL,
        [DdlTemplate] nvarchar(max) NOT NULL,
        [MaxPoolSize] int NOT NULL,
        CONSTRAINT [PK_TablesType] PRIMARY KEY ([TableTypeId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE TABLE [tpool].[TablesUsageHistory] (
        [HistoryId] bigint NOT NULL IDENTITY,
        [TablePoolId] bigint NOT NULL,
        [ConsumerId] varchar(100) NOT NULL,
        [BookedAtUtc] datetime2(2) NOT NULL,
        [ReleasedAtUtc] datetime2(2) NOT NULL,
        [ReleaseReason] varchar(50) NOT NULL DEFAULT 'Explicit',
        CONSTRAINT [PK_TablesUsageHistory] PRIMARY KEY ([HistoryId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE TABLE [tpool].[TablesPool] (
        [TablePoolId] bigint NOT NULL IDENTITY,
        [TableTypeId] int NOT NULL,
        [SchemaName] sysname NOT NULL DEFAULT 'tpool',
        [TableName] sysname NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TablesPool] PRIMARY KEY ([TablePoolId]),
        CONSTRAINT [FK_TablesPool_TablesType_TableTypeId] FOREIGN KEY ([TableTypeId]) REFERENCES [tpool].[TablesType] ([TableTypeId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE TABLE [tpool].[TablesUsage] (
        [TablePoolId] bigint NOT NULL,
        [ConsumerId] varchar(100) NOT NULL,
        [BookedAtUtc] datetime2(2) NOT NULL,
        [HeartbeatUtc] datetime2(2) NOT NULL,
        [DeadlineUtc] datetime2(2) NULL,
        CONSTRAINT [PK_TablesUsage] PRIMARY KEY ([TablePoolId]),
        CONSTRAINT [FK_TablesUsage_TablesPool_TablePoolId] FOREIGN KEY ([TablePoolId]) REFERENCES [tpool].[TablesPool] ([TablePoolId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_TablesPool_SchemaName_TableName] ON [tpool].[TablesPool] ([SchemaName], [TableName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE INDEX [IX_TablesPool_TableTypeId] ON [tpool].[TablesPool] ([TableTypeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_TablesType_TypeName] ON [tpool].[TablesType] ([TypeName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE INDEX [IX_TablesUsageHistory_BookedAtUtc] ON [tpool].[TablesUsageHistory] ([BookedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    CREATE INDEX [IX_TablesUsageHistory_TablePoolId] ON [tpool].[TablesUsageHistory] ([TablePoolId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [tpool].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260830162151_InitialTPoolSchema'
)
BEGIN
    INSERT INTO [tpool].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260830162151_InitialTPoolSchema', N'8.0.13');
END;
GO

COMMIT;
GO

