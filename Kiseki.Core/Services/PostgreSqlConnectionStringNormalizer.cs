using Npgsql;

namespace Kiseki.Core.Services;

/// <summary>
/// Normalizes PostgreSQL connection configurations from either standard ADO.NET keyword-value format
/// or PostgreSQL connection URIs (e.g. postgresql://user:pass@host:port/db?sslmode=require) into an
/// Npgsql-compatible ADO.NET connection string.
/// </summary>
public static class PostgreSqlConnectionStringNormalizer
{
    public static string Normalize(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        var trimmed = connectionString.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
            (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePostgreSqlUri(trimmed);
        }

        return NormalizeAdoNetConnectionString(trimmed);
    }

    private static string ParsePostgreSqlUri(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Invalid PostgreSQL connection URI format.", nameof(uriString));
        }

        var csb = new NpgsqlConnectionStringBuilder();

        // 1. Host
        if (string.IsNullOrWhiteSpace(uri.DnsSafeHost))
        {
            throw new ArgumentException("Host must be specified in PostgreSQL connection URI.", nameof(uriString));
        }
        csb.Host = uri.DnsSafeHost;

        // 2. Port: Preserve if explicitly specified; default to 5432 otherwise.
        csb.Port = uri.Port > 0 ? uri.Port : 5432;

        // 3. Username and Password (URL-decoded)
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            csb.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                csb.Password = Uri.UnescapeDataString(parts[1]);
            }
        }

        // 4. Database name (URL-decoded)
        var dbPath = uri.AbsolutePath.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            csb.Database = Uri.UnescapeDataString(dbPath);
        }

        // 5. Query parameters
        var sslModeExplicitlySet = false;
        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var queryString = uri.Query.TrimStart('?');
            var queryParams = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (var param in queryParams)
            {
                var kv = param.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]).Trim();
                var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]).Trim() : string.Empty;

                if (string.Equals(key, "sslmode", StringComparison.OrdinalIgnoreCase))
                {
                    csb.SslMode = ParseSslMode(val);
                    sslModeExplicitlySet = true;
                }
                else if (string.Equals(key, "pooling", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out var pooling))
                {
                    csb.Pooling = pooling;
                }
                else if (string.Equals(key, "timeout", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "connect_timeout", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(val, out var timeout))
                        csb.Timeout = timeout;
                }
                else if (string.Equals(key, "command_timeout", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "commandtimeout", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(val, out var cmdTimeout))
                        csb.CommandTimeout = cmdTimeout;
                }
                else if (string.Equals(key, "application_name", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "applicationname", StringComparison.OrdinalIgnoreCase))
                {
                    csb.ApplicationName = val;
                }
                else if (string.Equals(key, "search_path", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "searchpath", StringComparison.OrdinalIgnoreCase))
                {
                    csb.SearchPath = val;
                }
                else if (string.Equals(key, "channel_binding", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<ChannelBinding>(val, ignoreCase: true, out var cb))
                        csb.ChannelBinding = cb;
                }
                else
                {
                    try
                    {
                        csb[key] = val;
                    }
                    catch (ArgumentException)
                    {
                        // Ignore unsupported query parameters (e.g. endpoint=ep-xyz) to avoid failing on provider-specific options
                    }
                }
            }
        }

        ApplyCloudDefaults(csb, sslModeExplicitlySet);

        return csb.ConnectionString;
    }

    private static string NormalizeAdoNetConnectionString(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);

        var sslExplicit = connectionString.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase) ||
                          connectionString.Contains("SslMode", StringComparison.OrdinalIgnoreCase);

        ApplyCloudDefaults(csb, sslModeExplicitlySet: sslExplicit);

        return csb.ConnectionString;
    }

    private static void ApplyCloudDefaults(NpgsqlConnectionStringBuilder csb, bool sslModeExplicitlySet)
    {
        if (IsLocalHost(csb.Host))
            return;

        // Neon and remote cloud providers require SSL. Enforce SslMode.Require if not explicitly set.
        if (!sslModeExplicitlySet)
        {
            csb.SslMode = SslMode.Require;
        }

        // Send TCP keepalive probes every 30 seconds so intermediate firewalls/proxies don't kill idle sockets.
        if (csb.KeepAlive == 0)
        {
            csb.KeepAlive = 30;
        }

        // For cloud / serverless Postgres (like Neon or PgBouncer), prune connections idle for more than 15 seconds
        // so client pool does not attempt to reuse sockets severed by the server.
        if (csb.ConnectionIdleLifetime == 300)
        {
            csb.ConnectionIdleLifetime = 15;
        }

        // If connected through a connection pooler (e.g. Neon's PgBouncer with -pooler in hostname),
        // disable connection reset (DISCARD ALL) which causes PgBouncer transaction-mode disconnects.
        if (csb.Host != null && csb.Host.Contains("-pooler", StringComparison.OrdinalIgnoreCase))
        {
            csb.NoResetOnClose = true;
        }
    }

    private static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static SslMode ParseSslMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            "prefer" => SslMode.Prefer,
            "require" => SslMode.Require,
            "verify-ca" or "verify_ca" or "verifyca" => SslMode.VerifyCA,
            "verify-full" or "verify_full" or "verifyfull" => SslMode.VerifyFull,
            _ => Enum.TryParse<SslMode>(value, ignoreCase: true, out var mode)
                ? mode
                : SslMode.Require
        };
    }
}
