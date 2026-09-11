using System.Security.Claims;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly IAuthService _authService;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IAuthService authService, ILogger<LoginModel> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty]
    public bool RememberMe { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return GetSafeRedirectResult(ReturnUrl);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_authService.IsConfigured)
        {
            ErrorMessage = "Authentication is not configured on the server. Please set the KISEKI_PASSWORD environment variable.";
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }

        if (!_authService.ValidatePassword(Password))
        {
            ErrorMessage = "Invalid password. Please try again.";
            ModelState.AddModelError(string.Empty, ErrorMessage);
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Owner"),
            new(ClaimTypes.Role, "Admin")
        };

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = RememberMe,
            ExpiresUtc = RememberMe ? DateTimeOffset.UtcNow.AddDays(30) : null
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(claimsIdentity),
            authProperties);

        _logger.LogInformation("Successful login by owner.");

        return GetSafeRedirectResult(ReturnUrl);
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        _logger.LogInformation("Owner logged out.");
        return RedirectToPage("/Login");
    }

    private IActionResult GetSafeRedirectResult(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToPage("/Index");
    }
}
