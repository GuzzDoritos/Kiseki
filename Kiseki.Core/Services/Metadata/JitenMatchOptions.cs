namespace Kiseki.Core.Services.Metadata;

public sealed record JitenMatchOptions
{
    public int MaxConcurrency { get; init; } = 3;
    public int MaxRetries { get; init; } = 2;
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}
