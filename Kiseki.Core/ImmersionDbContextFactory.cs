using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kiseki.Core;

public class ImmersionDbContextFactory : IDesignTimeDbContextFactory<ImmersionDbContext>
{
    public ImmersionDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ImmersionDbContext>();
        // Design-time dummy connection string for EF Core tooling metadata generation
        optionsBuilder.UseNpgsql("Host=localhost;Database=kiseki_design;Username=postgres;Password=postgres");

        return new ImmersionDbContext(optionsBuilder.Options);
    }
}
