using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Spectre.Console;

namespace Kiseki.Console.Screens;

public sealed class TtsuImportScreen(TtsuImportService imports)
{
    public async Task RunAsync(TtsuBookContainer book, CancellationToken cancellationToken = default)
    {
        var match = await imports.MatchAsync(book, cancellationToken);
        AnsiConsole.MarkupLine(Markup.Escape(match.Reason));
        var targets = await imports.GetTargetsAsync(cancellationToken);
        var choices = targets.OrderByDescending(x => x.Id == match.WorkId)
            .Select(x => new TargetChoice(x.Id, $"Update {x.Title} ({x.Id.ToString()[..8]})")).ToList();
        choices.Add(new(null, "Create a new copy"));
        choices.Add(new(null, "Cancel", true));
        var target = AnsiConsole.Prompt(new SelectionPrompt<TargetChoice>().Title("Choose the import target")
            .UseConverter(x => Markup.Escape(x.Label)).AddChoices(choices).EnableSearch());
        if (target.Cancel) return;
        var orphanIds = new List<Guid>();
        var orphans = await imports.GetOrphansAsync(cancellationToken);
        if (target.Id is not null && orphans.Count > 0)
        {
            orphanIds = AnsiConsole.Prompt(new MultiSelectionPrompt<Kiseki.Core.Entities.ImmersionLog>()
                .Title("Assign unassigned TTSU logs belonging to this book (optional)").NotRequired()
                .UseConverter(x => $"{x.Date:yyyy-MM-dd}: {x.CharactersRead} chars / {x.TimeSpentMinutes:F2} min ({x.Id.ToString()[..8]})")
                .AddChoices(orphans)).Select(x => x.Id).ToList();
        }
        var resolutions = new Dictionary<DateOnly, string>();
        while (true)
        {
            var plan = await imports.PreviewAsync(book, target.Id, resolutions, orphanIds, cancellationToken);
            if (plan.Error is not null) { AnsiConsole.MarkupLine(Markup.Escape(plan.Error)); return; }
            var table = new Table().AddColumn("Date").AddColumn("Stored chars / minutes").AddColumn("Incoming chars / minutes").AddColumn("Action");
            foreach (var day in plan.Days)
                table.AddRow(day.Date.ToString("yyyy-MM-dd"), string.Join("; ", day.Existing.Select(x => $"{x.Characters} / {x.Minutes:F2}")),
                    string.Join("; ", day.Incoming.Select(x => $"{x.Characters} / {x.Minutes:F2}")), day.Action.ToString());
            AnsiConsole.Write(table);
            foreach (var day in plan.Days.Where(x => x.Action == TtsuDayAction.Conflict))
            {
                var options = day.Existing.Select(x => new ResolutionChoice($"keep:{x.Id}", $"Keep stored {x.Characters} chars / {x.Minutes:F2} min (revision {x.Revision?.ToString() ?? "unknown"})"))
                    .Concat(day.Incoming.Select((x, index) => new ResolutionChoice($"incoming:{index}", $"Use incoming baseline {x.Characters} chars / {x.Minutes:F2} min (revision {x.Revision?.ToString() ?? "unknown"})")))
                    .Append(new ResolutionChoice("cancel", "Cancel import")).ToList();
                var selected = AnsiConsole.Prompt(new SelectionPrompt<ResolutionChoice>()
                    .Title(Markup.Escape($"{day.Date:yyyy-MM-dd}: {day.ReviewReason}"))
                    .UseConverter(x => Markup.Escape(x.Label)).AddChoices(options));
                if (selected.Value == "cancel") return;
                resolutions[day.Date] = selected.Value;
            }
            if (!plan.CanApply) continue;
            AnsiConsole.MarkupLine($"Characters: {plan.CurrentCharacters:N0} -> {plan.ResultCharacters:N0}. Minutes: {plan.CurrentMinutes:F2} -> {plan.ResultMinutes:F2}.");
            if (!AnsiConsole.Confirm("Apply these changes?")) return;
            try
            {
                var result = await imports.ApplyAsync(Guid.NewGuid(), [new(book, target.Id, resolutions, plan.Fingerprint, orphanIds)], cancellationToken);
                AnsiConsole.MarkupLine($"[green]Imported: {result.AddedDays} new, {result.UpdatedDays} updated, {result.UnchangedDays} unchanged, {result.StaleDays} older days skipped.[/]");
                return;
            }
            catch (TtsuImportReviewRequiredException exception)
            {
                AnsiConsole.MarkupLine(Markup.Escape(exception.Message));
                resolutions.Clear();
            }
        }
    }
    private sealed record TargetChoice(Guid? Id, string Label, bool Cancel = false);
    private sealed record ResolutionChoice(string Value, string Label);
}
