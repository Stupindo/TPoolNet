namespace TPoolNet.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Design-time factory used by EF Core tooling (dotnet-ef migrations) to create a
/// <see cref="TPoolDbContext"/> without a running application host.
/// This class is only referenced at build/design time and is never called at runtime.
/// </summary>
internal sealed class TPoolDbContextFactory : IDesignTimeDbContextFactory<TPoolDbContext>
{
    public TPoolDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TPoolDbContext>();

        // Placeholder connection string used only for migration script generation.
        // The actual connection string is supplied via AddTPoolNet() at runtime.
        optionsBuilder.UseSqlServer(
            "Server=(local);Database=TPoolNet_Dev;Integrated Security=true;TrustServerCertificate=true;",
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "tpool"));

        return new TPoolDbContext(optionsBuilder.Options);
    }
}
