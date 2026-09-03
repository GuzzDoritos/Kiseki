using Core.Entities;
using Spectre.Console;

namespace ImmersionTracker.ConsoleApp.Display;

public static class MediaWorkTable
{
    public static void Write(IEnumerable<MediaWork> mediaWorks, string title = "Library")
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[green]{Markup.Escape(title)}[/]")
            .AddColumn("Type")
            .AddColumn("Series")
            .AddColumn("Title")
            .AddColumn(new TableColumn("Characters").RightAligned())
            .AddColumn(new TableColumn("Read").RightAligned())
            .AddColumn(new TableColumn("Progress").RightAligned())
            .AddColumn("Jiten");

        foreach (var mediaWork in mediaWorks)
        {
            table.AddRow(
                mediaWork.MediaType.ToString(),
                Markup.Escape(mediaWork.MediaSeries?.Title ?? "—"),
                Markup.Escape(mediaWork.Title),
                mediaWork.TotalCharacters.ToString("N0"),
                mediaWork.CurrentCharactersRead.ToString("N0"),
                $"{mediaWork.ProgressPercentage:N1}%",
                FormatJitenLink(mediaWork));
        }

        AnsiConsole.Write(table);
    }

    private static string FormatJitenLink(MediaWork mediaWork)
    {
        if (mediaWork.JitenDeckId is not int deckId)
        {
            return "[grey]Not linked[/]";
        }

        return mediaWork.JitenSubdeckId is int subdeckId
            ? $"Deck {deckId} / Subdeck {subdeckId}"
            : $"Deck {deckId}";
    }
}
