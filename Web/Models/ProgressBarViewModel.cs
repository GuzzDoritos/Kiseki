namespace Web.Models;

public sealed record ProgressBarViewModel(int Current, int Total, string? Label = null)
{
    public double Percentage => Total <= 0
        ? 0
        : Math.Min(100, (double)Current / Total * 100);
}
