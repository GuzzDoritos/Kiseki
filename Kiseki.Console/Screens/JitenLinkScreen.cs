using Kiseki.Console.Models;
using Kiseki.Core.DTOs;
using Kiseki.Core.Services;
using Spectre.Console;

namespace Kiseki.Console.Screens;

public sealed class JitenLinkScreen
{
    private readonly IJitenApiClient _jitenApiClient;

    public JitenLinkScreen(IJitenApiClient jitenApiClient)
    {
        _jitenApiClient = jitenApiClient;
    }

    public async Task<JitenMediaSelection?> SelectBookAsync(string? suggestedQuery = null)
    {
        var prompt = new TextPrompt<string>("Search query for [green]book[/]:");

        if (!string.IsNullOrWhiteSpace(suggestedQuery))
        {
            prompt.DefaultValue(suggestedQuery);
        }

        var query = AnsiConsole.Prompt(prompt);
        AnsiConsole.MarkupLine("[green]Querying Jiten...[/]");

        IReadOnlyList<JitenDeckDTO> results;

        try
        {
            results = await _jitenApiClient.SearchBooksAsync(query);
        }
        catch (HttpRequestException exception)
        {
            AnsiConsole.MarkupLine(
                $"[red]Jiten request failed:[/] {Markup.Escape(exception.Message)}");
            return null;
        }

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Jiten books were found.[/]");
            return null;
        }

        var selectedDeck = SelectDeck(results, "Select a [green]Jiten deck[/]:");

        if (selectedDeck is null)
        {
            return null;
        }

        if (selectedDeck.ChildrenDeckCount <= 0)
        {
            var selection = CreateDeckSelection(selectedDeck);
            WriteSelection(selection);
            return selection;
        }

        return await SelectDeckOrSubdeckAsync(selectedDeck);
    }

    private async Task<JitenMediaSelection?> SelectDeckOrSubdeckAsync(
        JitenDeckDTO selectedDeck)
    {
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(
                    $"[green]{Markup.Escape(GetTitle(selectedDeck))}[/] contains " +
                    $"{selectedDeck.ChildrenDeckCount} subdecks.")
                .AddChoices(
                    "Link the entire deck",
                    "Choose a subdeck",
                    "Cancel"));

        if (action == "Cancel")
        {
            return null;
        }

        if (action == "Link the entire deck")
        {
            var wholeDeck = CreateDeckSelection(selectedDeck);
            WriteSelection(wholeDeck);
            return wholeDeck;
        }

        JitenDeckDetailDTO? detail;

        try
        {
            detail = await _jitenApiClient.GetDeckDetailAsync(selectedDeck.DeckId);
        }
        catch (HttpRequestException exception)
        {
            AnsiConsole.MarkupLine(
                $"[red]Could not load subdecks:[/] {Markup.Escape(exception.Message)}");
            return null;
        }

        if (detail is null || detail.SubDecks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No subdecks were returned by Jiten.[/]");
            return null;
        }

        var selectedSubdeck = SelectDeck(
            detail.SubDecks,
            "Select a [green]Jiten subdeck[/]:");

        if (selectedSubdeck is null)
        {
            return null;
        }

        var selection = new JitenMediaSelection(
            selectedDeck.DeckId,
            selectedSubdeck.DeckId,
            GetTitle(selectedSubdeck),
            selectedSubdeck.CharacterCount);

        WriteSelection(selection);
        return selection;
    }

    private static JitenDeckDTO? SelectDeck(
        IEnumerable<JitenDeckDTO> decks,
        string promptTitle)
    {
        var options = decks
            .Select(deck => new DeckOption(deck))
            .Append(new DeckOption(null))
            .ToList();

        return AnsiConsole.Prompt(
            new SelectionPrompt<DeckOption>()
                .Title(promptTitle)
                .PageSize(15)
                .EnableSearch()
                .UseConverter(option => option.Deck is null
                    ? "[grey]Cancel[/]"
                    : FormatDeck(option.Deck))
                .AddChoices(options))
            .Deck;
    }

    private static string FormatDeck(JitenDeckDTO deck)
    {
        var children = deck.ChildrenDeckCount > 0
            ? $" [grey]({deck.ChildrenDeckCount} subdecks)[/]"
            : string.Empty;

        return $"{Markup.Escape(GetTitle(deck))} " +
               $"[yellow]{deck.CharacterCount:N0} chars[/]{children}";
    }

    private static JitenMediaSelection CreateDeckSelection(JitenDeckDTO deck)
    {
        return new JitenMediaSelection(
            deck.DeckId,
            null,
            GetTitle(deck),
            deck.CharacterCount);
    }

    private static string GetTitle(JitenDeckDTO deck)
    {
        if (!string.IsNullOrWhiteSpace(deck.OriginalTitle))
        {
            return deck.OriginalTitle;
        }

        if (!string.IsNullOrWhiteSpace(deck.EnglishTitle))
        {
            return deck.EnglishTitle;
        }

        return $"Jiten deck {deck.DeckId}";
    }

    private static void WriteSelection(JitenMediaSelection selection)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Title("[green]Jiten selection[/]")
            .AddColumn("Field")
            .AddColumn("Value");

        table.AddRow("Title", Markup.Escape(selection.Title));
        table.AddRow("Deck ID", selection.DeckId.ToString());
        table.AddRow("Subdeck ID", selection.SubdeckId?.ToString() ?? "—");
        table.AddRow(
            "Link type",
            selection.IsSubdeck ? "Specific subdeck" : "Entire deck");
        table.AddRow("Characters", selection.CharacterCount.ToString("N0"));

        AnsiConsole.Write(table);
    }

    private sealed record DeckOption(JitenDeckDTO? Deck);
}
