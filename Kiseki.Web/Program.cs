using Kiseki.Core;
using Kiseki.Core.Services;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

LoadDotEnvFile();

var builder = WebApplication.CreateBuilder(args);

var dbDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (!string.IsNullOrEmpty(dbDir))
{
    Directory.CreateDirectory(dbDir);
}

var rawDbPath = Environment.GetEnvironmentVariable("KISEKI_DB_PATH");
var databasePath = !string.IsNullOrEmpty(rawDbPath)
    ? Environment.ExpandEnvironmentVariables(rawDbPath)
    : Path.Join(dbDir, "kiseki.db");

var dbParent = Path.GetDirectoryName(databasePath);
if (!string.IsNullOrEmpty(dbParent))
{
    Directory.CreateDirectory(dbParent);
}

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
});
builder.Services.AddMemoryCache();
builder.Services.AddDbContext<ImmersionDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
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
    await context.Database.MigrateAsync();
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
