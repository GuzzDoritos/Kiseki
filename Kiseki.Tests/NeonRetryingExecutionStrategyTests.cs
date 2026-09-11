using System.Net.Sockets;
using Kiseki.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Kiseki.Tests;

public class NeonRetryingExecutionStrategyTests
{
    private sealed class TestStrategy : NeonRetryingExecutionStrategy
    {
        public TestStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies)
        {
        }

        public bool TestShouldRetryOn(Exception ex) => ShouldRetryOn(ex);
    }

    private static TestStrategy CreateStrategy()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkNpgsql();
        services.AddDbContext<DbContext>(options => options.UseNpgsql("Host=localhost;Database=test"));
        var provider = services.BuildServiceProvider();
        var dependencies = provider.GetRequiredService<ExecutionStrategyDependencies>();
        return new TestStrategy(dependencies);
    }

    [Fact]
    public void ShouldRetryOn_EndOfStreamException_ReturnsTrue()
    {
        var strategy = CreateStrategy();
        var ex = new NpgsqlException("Exception while reading from stream", new EndOfStreamException("Attempted to read past the end of the stream."));

        Assert.True(strategy.TestShouldRetryOn(ex));
    }

    [Fact]
    public void ShouldRetryOn_IOException_ReturnsTrue()
    {
        var strategy = CreateStrategy();
        var ex = new NpgsqlException("Exception while reading from stream", new IOException("Unable to read data from the transport connection."));

        Assert.True(strategy.TestShouldRetryOn(ex));
    }

    [Fact]
    public void ShouldRetryOn_SocketException_ReturnsTrue()
    {
        var strategy = CreateStrategy();
        var ex = new NpgsqlException("Socket error", new SocketException((int)SocketError.ConnectionReset));

        Assert.True(strategy.TestShouldRetryOn(ex));
    }

    [Fact]
    public void ShouldRetryOn_DirectEndOfStreamException_ReturnsTrue()
    {
        var strategy = CreateStrategy();
        var ex = new EndOfStreamException();

        Assert.True(strategy.TestShouldRetryOn(ex));
    }
}
