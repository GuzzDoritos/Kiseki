using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

public static class DatabaseInitializer
{
    public static async Task<string?> MigrateAsync(ImmersionDbContext context)
    {
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

        if (!pendingMigrations.Any())
        {
            return null;
        }

        string? backupPath = null;

        if (!string.IsNullOrWhiteSpace(context.DbPath) && File.Exists(context.DbPath))
        {
            backupPath = $"{context.DbPath}.backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(context.DbPath, backupPath);
        }

        await context.Database.MigrateAsync();
        return backupPath;
    }
}
