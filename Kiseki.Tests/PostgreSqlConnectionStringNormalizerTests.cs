using Kiseki.Web;
using Npgsql;
using Xunit;

namespace Kiseki.Tests;

public class PostgreSqlConnectionStringNormalizerTests
{
    [Fact]
    public void Normalize_StandardPostgreSqlUri_ProducesValidAdoNetString()
    {
        const string uri = "postgresql://myuser:mypass@ep-xyz.us-east-2.aws.neon.tech/neondb?sslmode=require";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("ep-xyz.us-east-2.aws.neon.tech", csb.Host);
        Assert.Equal(5432, csb.Port);
        Assert.Equal("myuser", csb.Username);
        Assert.Equal("mypass", csb.Password);
        Assert.Equal("neondb", csb.Database);
        Assert.Equal(SslMode.Require, csb.SslMode);
    }

    [Fact]
    public void Normalize_PostgresScheme_IsSupported()
    {
        const string uri = "postgres://alex:secret123@db.example.com/kisekiproddb?sslmode=require";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("db.example.com", csb.Host);
        Assert.Equal(5432, csb.Port);
        Assert.Equal("alex", csb.Username);
        Assert.Equal("secret123", csb.Password);
        Assert.Equal("kisekiproddb", csb.Database);
        Assert.Equal(SslMode.Require, csb.SslMode);
    }

    [Fact]
    public void Normalize_UrlEncodedComponents_DecodesCorrectly()
    {
        const string uri = "postgresql://user%40example.com:p%40ss%23123!@db.example.com/my%20db?sslmode=require";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("user@example.com", csb.Username);
        Assert.Equal("p@ss#123!", csb.Password);
        Assert.Equal("my db", csb.Database);
    }

    [Fact]
    public void Normalize_CustomPort_IsPreserved()
    {
        const string uri = "postgresql://user:pass@db.example.com:5433/mydb?sslmode=require";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(5433, csb.Port);
    }

    [Fact]
    public void Normalize_OmittedPort_DefaultsTo5432()
    {
        const string uri = "postgresql://user:pass@db.example.com/mydb?sslmode=require";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(5432, csb.Port);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify_ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    [InlineData("verify_full", SslMode.VerifyFull)]
    public void Normalize_SslModeVariations_MappedAppropriately(string sslModeInput, SslMode expected)
    {
        var uri = $"postgresql://user:pass@db.example.com:5432/mydb?sslmode={sslModeInput}";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(expected, csb.SslMode);
    }

    [Fact]
    public void Normalize_RemoteHostWithoutSslMode_EnforcesSslRequire()
    {
        const string uri = "postgresql://user:pass@ep-xyz.aws.neon.tech/neondb";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(uri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal(SslMode.Require, csb.SslMode);
    }

    [Fact]
    public void Normalize_StandardAdoNetFormat_IsSupported()
    {
        const string adoNet = "Host=ep-xyz.neon.tech;Port=5432;Database=neondb;Username=myuser;Password=mypass;SSL Mode=Require";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(adoNet);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("ep-xyz.neon.tech", csb.Host);
        Assert.Equal(5432, csb.Port);
        Assert.Equal("myuser", csb.Username);
        Assert.Equal("mypass", csb.Password);
        Assert.Equal("neondb", csb.Database);
        Assert.Equal(SslMode.Require, csb.SslMode);
    }

    [Fact]
    public void Normalize_QuotedString_StripsQuotesAndParses()
    {
        const string quotedUri = "\"postgresql://user:pass@db.example.com/mydb?sslmode=require\"";

        var result = PostgreSqlConnectionStringNormalizer.Normalize(quotedUri);
        var csb = new NpgsqlConnectionStringBuilder(result);

        Assert.Equal("db.example.com", csb.Host);
        Assert.Equal("mydb", csb.Database);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrWhitespace_ThrowsArgumentException(string? invalidInput)
    {
        Assert.Throws<ArgumentException>(() => PostgreSqlConnectionStringNormalizer.Normalize(invalidInput!));
    }
}
