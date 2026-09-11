using Kiseki.Core;
using Kiseki.Core.Services;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (!string.IsNullOrEmpty(dbDir))
{
    Directory.CreateDirectory(dbDir);
}

var databasePath = Environment.GetEnvironmentVariable("KISEKI_DB_PATH")
    ?? Path.Join(dbDir, "kiseki.db");

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
