using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hookline.Api.Data;

/// <summary>Lets <c>dotnet ef</c> build the model for migrations without starting the app or a database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<HooklineDbContext>
{
    public HooklineDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<HooklineDbContext>()
        .UseNpgsql("Host=localhost;Database=hookline")
        .UseSnakeCaseNamingConvention()
        .Options);
}
