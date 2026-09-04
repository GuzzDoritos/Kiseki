using Kiseki.Console.Display;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;

namespace Kiseki.Console.Screens;

public sealed class LibraryScreen
{
    private readonly ImmersionDbContext _context;
    private readonly JitenLinkScreen _jitenLinkScreen;

    public LibraryScreen(
        ImmersionDbContext context,
        JitenLinkScreen jitenLinkScreen)
    {
        _context = context;
        _jitenLinkScreen = jitenLinkScreen;
    }

    public async Task ShowAsync()
    {
        while (true)
        {
            var mediaWorks = await LoadMediaWorksAsync();

            if (mediaWorks.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Your library is empty.[/]");
                return;
            }

            MediaWorkTable.Write(mediaWorks);

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .AddChoices("Edit entry", "Delete entry", "Go back"));

            if (action == "Go back")
            {
                return;
            }

            var selectedWork = SelectMediaWork(mediaWorks);

            if (action == "Edit entry")
            {
                await EditAsync(selectedWork);
            }
            else
            {
                await DeleteAsync(selectedWork);
            }
        }
    }

    private Task<List<MediaWork>> LoadMediaWorksAsync()
    {
        return _context.MediaWorks
            .Include(mediaWork => mediaWork.Logs)
            .Include(mediaWork => mediaWork.MediaSeries)
            .OrderBy(mediaWork => mediaWork.MediaType)
            .ThenBy(mediaWork => mediaWork.MediaSeries != null
                ? mediaWork.MediaSeries.Title
                : string.Empty)
            .ThenBy(mediaWork => mediaWork.Title)
            .ToListAsync();
    }

    private static MediaWork SelectMediaWork(IEnumerable<MediaWork> mediaWorks)
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<MediaWork>()
                .Title("Select a [green]library entry[/]:")
                .UseConverter(mediaWork =>
                    $"[[{mediaWork.MediaType}]] {Markup.Escape(mediaWork.Title)} " +
                    $"({mediaWork.Id.ToString()[..8]})")
                .AddChoices(mediaWorks)
                .EnableSearch());
    }

    private async Task EditAsync(MediaWork mediaWork)
    {
        while (true)
        {
            var field = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Edit [green]{Markup.Escape(mediaWork.Title)}[/]:")
                    .AddChoices(
                        "Title",
                        "Media type",
                        "Series",
                        "Link to Jiten",
                        "Remove Jiten link",
                        "Manual character override",
                        "Completed status",
                        "Done"));

            if (field == "Done")
            {
                return;
            }

            var changed = await EditFieldAsync(mediaWork, field);

            if (changed)
            {
                await _context.SaveChangesAsync();
                AnsiConsole.MarkupLine("[green]Entry updated.[/]");
            }
        }
    }

    private async Task<bool> EditFieldAsync(MediaWork mediaWork, string field)
    {
        switch (field)
        {
            case "Title":
                mediaWork.Title = PromptForTitle(mediaWork.Title);
                return true;

            case "Media type":
                var mediaType = AnsiConsole.Prompt(
                    new SelectionPrompt<MediaType>()
                        .Title("Select the [green]media type[/]:")
                        .AddChoices(Enum.GetValues<MediaType>()));

                if (mediaWork.MediaSeries?.MediaType != mediaType)
                {
                    mediaWork.MediaSeries = null;
                    mediaWork.MediaSeriesId = null;
                }

                mediaWork.MediaType = mediaType;
                return true;

            case "Series":
                return await AssignSeriesAsync(mediaWork);

            case "Link to Jiten":
                return await LinkToJitenAsync(mediaWork);

            case "Remove Jiten link":
                if (!mediaWork.HasJitenLink)
                {
                    AnsiConsole.MarkupLine("[yellow]This entry is not linked to Jiten.[/]");
                    return false;
                }

                if (!AnsiConsole.Confirm("Remove this entry's Jiten link?", false))
                {
                    return false;
                }

                mediaWork.RemoveJitenLink();
                return true;

            case "Manual character override":
                mediaWork.ManualCharacterCountOverride = PromptOptionalNumber(
                    "Manual character override", mediaWork.ManualCharacterCountOverride);
                return true;

            case "Completed status":
                mediaWork.IsCompleted = AnsiConsole.Confirm(
                    "Mark this entry as completed?", mediaWork.IsCompleted);
                return true;

            default:
                return false;
        }
    }

    private async Task<bool> LinkToJitenAsync(MediaWork mediaWork)
    {
        if (mediaWork.MediaType != MediaType.Book)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Jiten linking currently supports books only.[/]");
            return false;
        }

        var selection = await _jitenLinkScreen.SelectBookAsync(mediaWork.Title);

        if (selection is null)
        {
            return false;
        }

        var question = mediaWork.HasJitenLink
            ? "Replace the existing Jiten link with this selection?"
            : "Link this entry to the selection?";

        if (!AnsiConsole.Confirm(question))
        {
            return false;
        }

        selection.ApplyTo(mediaWork);

        if (selection.IsSubdeck && mediaWork.MediaSeries is not null &&
            mediaWork.MediaSeries.JitenDeckId is null &&
            AnsiConsole.Confirm("Use the parent Jiten deck as this series' Jiten deck?"))
        {
            mediaWork.MediaSeries.JitenDeckId = selection.DeckId;
        }

        return true;
    }

    private async Task<bool> AssignSeriesAsync(MediaWork mediaWork)
    {
        var series = await _context.MediaSeries
            .Where(item => item.MediaType == mediaWork.MediaType)
            .OrderBy(item => item.Title)
            .ToListAsync();

        var options = series
            .Select(item => new SeriesOption(item.Title, item))
            .Prepend(new SeriesOption("Create a new series", null, CreatesSeries: true))
            .Append(new SeriesOption("Remove from current series", null, RemovesSeries: true))
            .Append(new SeriesOption("Cancel", null))
            .ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<SeriesOption>()
                .Title("Select a [green]series[/]:")
                .EnableSearch()
                .UseConverter(option => Markup.Escape(option.Label))
                .AddChoices(options));

        if (choice.CreatesSeries)
        {
            var title = PromptForTitle(mediaWork.Title);
            var newSeries = new MediaSeries(title, mediaWork.MediaType);
            _context.MediaSeries.Add(newSeries);
            mediaWork.MediaSeries = newSeries;

            if (mediaWork.IsLinkedToJitenSubdeck && mediaWork.JitenDeckId.HasValue)
            {
                newSeries.JitenDeckId = mediaWork.JitenDeckId;
            }

            return true;
        }

        if (choice.RemovesSeries)
        {
            mediaWork.MediaSeries = null;
            mediaWork.MediaSeriesId = null;
            return true;
        }

        if (choice.Series is null)
        {
            return false;
        }

        mediaWork.MediaSeries = choice.Series;
        return true;
    }

    private static string PromptForTitle(string currentTitle)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Title[/]:")
                .DefaultValue(currentTitle)
                .Validate(title => !string.IsNullOrWhiteSpace(title)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Title cannot be empty.[/]")));
    }

    private static int? PromptOptionalNumber(string fieldName, int? currentValue)
    {
        var current = currentValue?.ToString("N0") ?? "not set";
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>(
                    $"[green]{Markup.Escape(fieldName)}[/] " +
                    $"(currently {current}; leave empty to clear):")
                .AllowEmpty()
                .Validate(value =>
                    string.IsNullOrWhiteSpace(value) ||
                    (int.TryParse(value, out var parsed) && parsed >= 0)
                        ? ValidationResult.Success()
                        : ValidationResult.Error(
                            "[red]Enter a non-negative whole number or leave it empty.[/]")));

        return string.IsNullOrWhiteSpace(input) ? null : int.Parse(input);
    }

    private async Task DeleteAsync(MediaWork mediaWork)
    {
        var confirmed = AnsiConsole.Confirm(
            $"Delete [red]{Markup.Escape(mediaWork.Title)}[/] and its " +
            $"[red]{mediaWork.Logs.Count} immersion log(s)[/]?",
            false);

        if (!confirmed)
        {
            return;
        }

        _context.ImmersionLogs.RemoveRange(mediaWork.Logs);
        _context.MediaWorks.Remove(mediaWork);
        await _context.SaveChangesAsync();

        AnsiConsole.MarkupLine("[green]Library entry deleted.[/]");
    }

    private sealed record SeriesOption(
        string Label,
        MediaSeries? Series,
        bool CreatesSeries = false,
        bool RemovesSeries = false);
}
