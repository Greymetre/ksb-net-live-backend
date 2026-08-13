using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("KSB_PR_CONNECTION")
            ?? Environment.GetEnvironmentVariable("SQLSERVER_CONNECTIONSTRING")
            ?? Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTIONSTRING")
            ?? "Server=localhost,1433;Database=netksb_new;User Id=sa;Password=Your_password123;Encrypt=False;TrustServerCertificate=True;";

        optionsBuilder.UseSqlServer(
            connectionString,
            sqlServerOptions => sqlServerOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null));
        return new AppDbContext(optionsBuilder.Options);
    }
}
