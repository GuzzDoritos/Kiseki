namespace Kiseki.Web.Models;

public sealed record ProgressBarViewModel(
    int Current,
    int Total,
    string? Label = null,
    bool IsComplete = false,
    bool ShowPercentage = true)
{
    public double Percentage => IsComplete
        ? 100
        : Total <= 0
            ? 0
            : Math.Min(100, (double)Current / Total * 100);
}
