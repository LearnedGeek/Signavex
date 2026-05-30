using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Signavex.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core CLI tooling (<c>dotnet ef migrations add</c>,
/// <c>dotnet ef database update</c>). The host and credentials are placeholders —
/// these need to match a real Postgres only when running <c>database update</c>;
/// migration scaffolding works against the model alone.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SignavexDbContext>
{
    public SignavexDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SignavexDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=signavex_design;Username=signavex;Password=design-time-only");

        return new SignavexDbContext(optionsBuilder.Options);
    }
}
