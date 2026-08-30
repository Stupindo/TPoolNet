namespace TPoolNet.Data;

using Microsoft.EntityFrameworkCore;
using TPoolNet.Entities;

/// <summary>
/// EF Core DbContext for TPoolNet metadata tables.
/// All tables reside in the <c>[tpool]</c> SQL Server schema.
/// </summary>
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
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("tpool");

        // ─── TablesType ───────────────────────────────────────────────────────────
        modelBuilder.Entity<TablesType>(b =>
        {
            b.ToTable("TablesType");
            b.HasKey(e => e.TableTypeId);
            b.Property(e => e.TypeName).HasColumnType("varchar(50)").HasMaxLength(50).IsRequired();
            b.HasIndex(e => e.TypeName).IsUnique();
            b.Property(e => e.TablePrefix).HasColumnType("varchar(50)").HasMaxLength(50).IsRequired();
            b.Property(e => e.DdlTemplate).HasColumnType("nvarchar(max)").IsRequired();
            b.Property(e => e.MaxPoolSize).IsRequired();
        });

        // ─── TablesConsumerType ───────────────────────────────────────────────────
        modelBuilder.Entity<TablesConsumerType>(b =>
        {
            b.ToTable("TablesConsumerType");
            b.HasKey(e => e.ConsumerTypeId);
            b.Property(e => e.ConsumerName).HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
            b.Property(e => e.Description).HasColumnType("varchar(500)").HasMaxLength(500);
        });

        // ─── TablesPool ───────────────────────────────────────────────────────────
        modelBuilder.Entity<TablesPool>(b =>
        {
            b.ToTable("TablesPool");
            b.HasKey(e => e.TablePoolId);
            b.Property(e => e.TablePoolId).UseIdentityColumn();

            // SYSNAME is an alias for nvarchar(128) NOT NULL in SQL Server
            b.Property(e => e.SchemaName).HasColumnType("sysname").HasMaxLength(128).HasDefaultValue("tpool");
            b.Property(e => e.TableName).HasColumnType("sysname").HasMaxLength(128).IsRequired();
            b.HasIndex(e => new { e.SchemaName, e.TableName }).IsUnique();

            b.HasOne(e => e.TableType)
             .WithMany(t => t.Tables)
             .HasForeignKey(e => e.TableTypeId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ─── TablesUsage ──────────────────────────────────────────────────────────
        modelBuilder.Entity<TablesUsage>(b =>
        {
            b.ToTable("TablesUsage");
            b.HasKey(e => e.TablePoolId);

            // No identity — TablePoolId is shared PK with TablesPool (1:1)
            b.Property(e => e.TablePoolId).ValueGeneratedNever();
            b.Property(e => e.ConsumerId).HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
            b.Property(e => e.BookedAtUtc).HasColumnType("datetime2(2)").HasPrecision(2);
            b.Property(e => e.HeartbeatUtc).HasColumnType("datetime2(2)").HasPrecision(2);
            b.Property(e => e.DeadlineUtc).HasColumnType("datetime2(2)").HasPrecision(2);

            b.HasOne(e => e.TablePool)
             .WithOne(p => p.ActiveUsage)
             .HasForeignKey<TablesUsage>(e => e.TablePoolId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── TablesUsageHistory ───────────────────────────────────────────────────
        modelBuilder.Entity<TablesUsageHistory>(b =>
        {
            b.ToTable("TablesUsageHistory");
            b.HasKey(e => e.HistoryId);
            b.Property(e => e.HistoryId).UseIdentityColumn();
            b.Property(e => e.ConsumerId).HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
            b.Property(e => e.BookedAtUtc).HasColumnType("datetime2(2)").HasPrecision(2);
            b.Property(e => e.ReleasedAtUtc).HasColumnType("datetime2(2)").HasPrecision(2);
            b.Property(e => e.ReleaseReason).HasColumnType("varchar(50)").HasMaxLength(50).IsRequired()
             .HasDefaultValue("Explicit");
            b.HasIndex(e => e.TablePoolId);
            b.HasIndex(e => e.BookedAtUtc);
        });
    }
}
