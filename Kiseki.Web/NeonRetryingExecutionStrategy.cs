using System.Net.Sockets;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Kiseki.Web;

/// <summary>
/// Custom execution strategy for Neon serverless PostgreSQL and cloud environments.
/// Extends NpgsqlRetryingExecutionStrategy to also treat socket breaks, EndOfStreamException,
/// and connection drops (e.g. from serverless compute scale-to-zero or PgBouncer timeouts) as retryable transient errors.
/// </summary>
public class NeonRetryingExecutionStrategy : NpgsqlRetryingExecutionStrategy
{
    public NeonRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(dependencies, maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)
    {
    }

    protected override bool ShouldRetryOn(Exception? exception)
    {
        if (exception is null)
            return false;

        // If Npgsql encountered an unexpected end of stream or network termination from serverless sleep / pooler
        if (exception is NpgsqlException npgsqlException)
        {
            if (npgsqlException.InnerException is EndOfStreamException or IOException or SocketException)
            {
                return true;
            }
        }

        if (exception is EndOfStreamException or IOException or SocketException)
        {
            return true;
        }

        return base.ShouldRetryOn(exception);
    }
}
