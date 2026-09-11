namespace Kiseki.Web;

public static class PostgreSqlConnectionStringNormalizer
{
    public static string Normalize(string connectionString) => Kiseki.Core.Services.PostgreSqlConnectionStringNormalizer.Normalize(connectionString);
}
