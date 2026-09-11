using System.Security.Cryptography;
using System.Text;

namespace Kiseki.Web.Services;

public interface IAuthService
{
    bool IsConfigured { get; }
    bool ValidatePassword(string? inputPassword);
}

public sealed class SinglePasswordAuthService : IAuthService
{
    private readonly string? _expectedPassword;
    private readonly bool _isDevelopment;
    private readonly ILogger<SinglePasswordAuthService> _logger;

    public SinglePasswordAuthService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<SinglePasswordAuthService> logger)
    {
        _logger = logger;
        _isDevelopment = environment.IsDevelopment();

        // Priority: KISEKI_PASSWORD env var -> Auth:Password config
        var envPassword = Environment.GetEnvironmentVariable("KISEKI_PASSWORD");
        var configPassword = configuration["Auth:Password"];

        _expectedPassword = !string.IsNullOrWhiteSpace(envPassword)
            ? envPassword
            : configPassword;

        if (string.IsNullOrWhiteSpace(_expectedPassword))
        {
            if (_isDevelopment)
            {
                // In local development, fall back to "kiseki" to avoid blocking local workflows
                _expectedPassword = "kiseki";
                _logger.LogInformation("KISEKI_PASSWORD is not set. Defaulting to 'kiseki' in Development mode.");
            }
            else
            {
                _logger.LogWarning("KISEKI_PASSWORD is not set in Production environment! All authentication attempts will be rejected.");
                _expectedPassword = null;
            }
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_expectedPassword);

    public bool ValidatePassword(string? inputPassword)
    {
        if (string.IsNullOrEmpty(_expectedPassword) || string.IsNullOrEmpty(inputPassword))
        {
            return false;
        }

        var inputBytes = Encoding.UTF8.GetBytes(inputPassword);
        var expectedBytes = Encoding.UTF8.GetBytes(_expectedPassword);

        return CryptographicOperations.FixedTimeEquals(inputBytes, expectedBytes);
    }
}
