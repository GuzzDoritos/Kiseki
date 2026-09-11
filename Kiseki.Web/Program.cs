using Kiseki.Core;
using Kiseki.Core.Services;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

LoadDotEnvFile();

var builder = WebApplication.CreateBuilder(args);

// Configure Database Provider (PostgreSQL for Render/Neon, SQLite for local development)
var provider = builder.Configuration["DatabaseProvider"]?.Trim().ToLowerInvariant();

if (string.IsNullOrEmpty(provider))
{
    // If not explicitly set: default to postgres in production or if DefaultConnection is set, otherwise sqlite
    if (!string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")) ||
        !builder.Environment.IsDevelopment())
    {
        provider = "postgres";
    }
    else
    {
        provider = "sqlite";
    }
}

if (provider is "postgres" or "postgresql" or "npgsql")
{
    var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? builder.Configuration["DATABASE_URL"];

    if (string.IsNullOrWhiteSpace(rawConnectionString))
    {
        throw new InvalidOperationException(
            "PostgreSQL provider was selected, but connection string 'DefaultConnection' was not found. " +
            "Please configure 'ConnectionStrings:DefaultConnection' in configuration or set the " +
            "'ConnectionStrings__DefaultConnection' environment variable (e.g. from Neon).");
    }

    var connectionString = NormalizePostgreSqlConnectionString(rawConnectionString);

    builder.Services.AddDbContext<ImmersionDbContext>(options =>
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null);
        }));
}
else if (provider == "sqlite")
{
    var rawDbPath = builder.Configuration["KISEKI_DB_PATH"]
        ?? Environment.GetEnvironmentVariable("KISEKI_DB_PATH");

    string sqlitePath;
    if (!string.IsNullOrWhiteSpace(rawDbPath))
    {
        sqlitePath = Environment.ExpandEnvironmentVariables(rawDbPath);
        if (!Path.IsPathRooted(sqlitePath))
        {
            sqlitePath = Path.GetFullPath(sqlitePath);
        }
    }
    else
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        sqlitePath = Path.Join(localAppData, "kiseki.db");
    }

    var dbDirectory = Path.GetDirectoryName(sqlitePath);
    if (!string.IsNullOrEmpty(dbDirectory))
    {
        Directory.CreateDirectory(dbDirectory);
    }

    builder.Services.AddDbContext<ImmersionDbContext>(options =>
        options.UseSqlite($"Data Source={sqlitePath}"));
}
else
{
    throw new InvalidOperationException(
        $"Unsupported DatabaseProvider: '{provider}'. Supported providers are 'sqlite' and 'postgres'.");
}

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
});
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IJitenApiClient, JitenApiClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<TtsuDataLoader>();
builder.Services.AddSingleton<ITtsuImportBatchStore, TtsuImportBatchStore>();
builder.Services.AddSingleton<IAuthService, SinglePasswordAuthService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Kiseki.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ImmersionDbContext>();
    if (context.Database.IsNpgsql())
    {
        // For PostgreSQL (Production / Neon):
        // Run authoritative EF Core migrations
        await context.Database.MigrateAsync();
    }
    else if (context.Database.IsSqlite())
    {
        // For local SQLite development:
        // EnsureCreatedAsync provisions tables if the database does not exist,
        // and does nothing if the database (and tables) already exist.
        // It NEVER drops, deletes, or overwrites existing data or migrations history.
        await context.Database.EnsureCreatedAsync();
    }
}

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

static void LoadDotEnvFile()
{
    var current = Directory.GetCurrentDirectory();
    string[] candidates = [
        Path.Combine(current, ".env"),
        Path.Combine(current, "..", ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env")
    ];

    foreach (var path in candidates)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            foreach (var line in File.ReadAllLines(fullPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var idx = trimmed.IndexOf('=');
                if (idx > 0)
                {
                    var key = trimmed[..idx].Trim();
                    var val = trimmed[(idx + 1)..].Trim();
                    if ((val.StartsWith('"') && val.EndsWith('"')) ||
                        (val.StartsWith('\'') && val.EndsWith('\'')))
                    {
                        val = val[1..^1];
                    }
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, val);
                    }
                }
            }
            break;
        }
    }
}

static string NormalizePostgreSqlConnectionString(string connectionString)
    => Kiseki.Web.PostgreSqlConnectionStringNormalizer.Normalize(connectionString);

