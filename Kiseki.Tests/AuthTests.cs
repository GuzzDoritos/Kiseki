using System.Security.Claims;
using Kiseki.Web.Pages;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kiseki.Tests;

public sealed class AuthTests
{
    private sealed class StubHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Kiseki.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void SinglePasswordAuthService_ValidatesCorrectPassword()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Password"] = "MySuperSecret123!"
            })
            .Build();

        var env = new StubHostEnvironment { EnvironmentName = "Production" };
        var service = new SinglePasswordAuthService(config, env, NullLogger<SinglePasswordAuthService>.Instance);

        Assert.True(service.IsConfigured);
        Assert.True(service.ValidatePassword("MySuperSecret123!"));
        Assert.False(service.ValidatePassword("WrongPassword"));
        Assert.False(service.ValidatePassword(null));
        Assert.False(service.ValidatePassword(string.Empty));
    }

    [Fact]
    public void SinglePasswordAuthService_InDevelopment_FallsBackToKiseki()
    {
        var config = new ConfigurationBuilder().Build();
        var env = new StubHostEnvironment { EnvironmentName = "Development" };
        var service = new SinglePasswordAuthService(config, env, NullLogger<SinglePasswordAuthService>.Instance);

        Assert.True(service.IsConfigured);
        Assert.True(service.ValidatePassword("kiseki"));
        Assert.False(service.ValidatePassword("wrong"));
    }

    [Fact]
    public void SinglePasswordAuthService_InProduction_FailsClosedWhenNotConfigured()
    {
        var config = new ConfigurationBuilder().Build();
        var env = new StubHostEnvironment { EnvironmentName = "Production" };
        var service = new SinglePasswordAuthService(config, env, NullLogger<SinglePasswordAuthService>.Instance);

        Assert.False(service.IsConfigured);
        Assert.False(service.ValidatePassword("kiseki"));
        Assert.False(service.ValidatePassword("any"));
    }

    [Fact]
    public async Task LoginModel_OnPost_RejectsInvalidPassword()
    {
        var authService = new StubAuthService(isConfigured: true, isValid: false);
        var page = CreateLoginModel(authService);
        page.Password = "wrong-pw";

        var result = await page.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(page.ModelState.IsValid);
        Assert.Equal("Invalid password. Please try again.", page.ErrorMessage);
    }

    [Fact]
    public async Task LoginModel_OnPost_FailsIfServiceNotConfigured()
    {
        var authService = new StubAuthService(isConfigured: false, isValid: false);
        var page = CreateLoginModel(authService);
        page.Password = "any-pw";

        var result = await page.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(page.ModelState.IsValid);
        Assert.Contains("not configured", page.ErrorMessage);
    }

    [Fact]
    public void LoginModel_OnGet_RedirectsIfAlreadyAuthenticated()
    {
        var authService = new StubAuthService(isConfigured: true, isValid: true);
        var page = CreateLoginModel(authService, isAuthenticated: true);
        page.ReturnUrl = "/Library";

        var result = page.OnGet();

        var redirectResult = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Library", redirectResult.Url);
    }

    [Fact]
    public async Task LoginModel_SafeRedirect_PreventsOpenRedirect()
    {
        var authService = new StubAuthService(isConfigured: true, isValid: true);
        var page = CreateLoginModel(authService);
        page.Password = "valid";
        page.ReturnUrl = "http://malicious-phishing.com";

        var result = await page.OnPostAsync();

        var redirectResult = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Index", redirectResult.PageName);
    }

    [Fact]
    public async Task LoginModel_OnPostLogout_RedirectsToLogin()
    {
        var authService = new StubAuthService(isConfigured: true, isValid: true);
        var page = CreateLoginModel(authService, isAuthenticated: true);

        var result = await page.OnPostLogoutAsync();

        var redirectResult = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Login", redirectResult.PageName);
    }

    private static LoginModel CreateLoginModel(
        IAuthService authService,
        bool isAuthenticated = false)
    {
        var httpContext = new DefaultHttpContext();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);

        httpContext.RequestServices = services.BuildServiceProvider();

        if (isAuthenticated)
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "Owner") },
                CookieAuthenticationDefaults.AuthenticationScheme);
            httpContext.User = new ClaimsPrincipal(identity);
        }

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new PageActionDescriptor(),
            new ModelStateDictionary());

        var modelState = new ModelStateDictionary();
        var pageContext = new PageContext(actionContext)
        {
            ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary(
                new EmptyModelMetadataProvider(),
                modelState)
        };

        var page = new LoginModel(authService, NullLogger<LoginModel>.Instance)
        {
            PageContext = pageContext,
            Url = new StubUrlHelper(actionContext)
        };

        return page;
    }

    private sealed class StubAuthService : IAuthService
    {
        private readonly bool _isConfigured;
        private readonly bool _isValid;

        public StubAuthService(bool isConfigured, bool isValid)
        {
            _isConfigured = isConfigured;
            _isValid = isValid;
        }

        public bool IsConfigured => _isConfigured;
        public bool ValidatePassword(string? inputPassword) => _isValid;
    }

    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; }

        public StubUrlHelper(ActionContext actionContext)
        {
            ActionContext = actionContext;
        }

        public string? Action(UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            // Local urls start with / and not // or /\
            return url.StartsWith('/') && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
        }
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
