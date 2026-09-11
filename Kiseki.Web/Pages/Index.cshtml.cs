using Kiseki.Core;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages;

public class IndexModel(ImmersionDbContext dbContext) : PageModel
{
    public int WeekCount { get; set; }
    public string WeekTime { get; set; } = "00h00m";
    public int YearCount { get; set; }
    public string YearTime { get; set; } = "00h00m";
    public int ActiveWorksCount { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        int daysFromMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var startOfWeek = today.AddDays(-daysFromMonday);
        var endOfWeek = startOfWeek.AddDays(6);

        var weekLogsQuery = dbContext.ImmersionLogs
            .AsNoTracking()
            .Where(log => log.Date >= startOfWeek && log.Date <= endOfWeek);

        WeekCount = await weekLogsQuery
            .SumAsync(log => log.CharactersRead, cancellationToken);

        var totalWeekMinutes = await weekLogsQuery
            .SumAsync(log => log.TimeSpentMinutes, cancellationToken);

        var weekTime = TimeSpan.FromSeconds(Math.Max(0, Math.Round(totalWeekMinutes * 60)));
        WeekTime = $"{(int)weekTime.TotalHours:D2}h{weekTime.Minutes:D2}m";

        var startOfYear = new DateOnly(today.Year, 1, 1);
        var endOfYear = new DateOnly(today.Year, 12, 31);

        var yearLogsQuery = dbContext.ImmersionLogs
            .AsNoTracking()
            .Where(log => log.Date >= startOfYear && log.Date <= endOfYear);

        YearCount = await yearLogsQuery
            .SumAsync(log => log.CharactersRead, cancellationToken);

        var totalYearMinutes = await yearLogsQuery
            .SumAsync(log => log.TimeSpentMinutes, cancellationToken);

        var yearTime = TimeSpan.FromSeconds(Math.Max(0, Math.Round(totalYearMinutes * 60)));
        YearTime = $"{(int)yearTime.TotalHours:D2}h{yearTime.Minutes:D2}m";

        ActiveWorksCount = await dbContext.MediaWorks
            .AsNoTracking()
            .CountAsync(work => !work.IsCompleted, cancellationToken);
    }
}
