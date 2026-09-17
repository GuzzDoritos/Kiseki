using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Spectre.Console;

namespace Kiseki.Console.Screens;

public sealed class JitenLinkScreen
{
    private readonly IJitenApiClient _jitenApiClient;
    private readonly IJitenSelectionResolver _selectionResolver;
    private readonly IAnsiConsole _console;

    public JitenLinkScreen(
        IJitenApiClient jitenApiClient,
        IJitenSelectionResolver? selectionResolver = null,
        IAnsiConsole? console = null)
    {
        _jitenApiClient = jitenApiClient ?? throw new ArgumentNullException(nameof(jitenApiClient));
        _selectionResolver = selectionResolver ?? new JitenSelectionResolver(jitenApiClient);
        _console = console ?? AnsiConsole.Console;
    }

    public async Task<JitenMediaSelection?> SelectBookAsync(string? suggestedQuery = null)
    {
        var prompt = new TextPrompt<string>("Search query for [green]book[/]:");

        if (!string.IsNullOrWhiteSpace(suggestedQuery))
        {
            prompt.DefaultValue(suggestedQuery);
        }

        var query = _console.Prompt(prompt);
        _console.MarkupLine("[green]Querying Jiten...[/]");

        IReadOnlyList<JitenDeckDTO> results;

        try
        {
            results = await _jitenApiClient.SearchBooksAsync(query);
        }
        catch (HttpRequestException exception)
        {
            _console.MarkupLine(
                $"[red]Jiten request failed:[/] {Markup.Escape(exception.Message)}");
            return null;
        }

        if (results.Count == 0)
        {
            _console.MarkupLine("[yellow]No Jiten books were found.[/]");
            return null;
        }

        var selectedDeck = SelectDeck(results, "Select a [green]Jiten deck[/]:");

        if (selectedDeck is null)
        {
            return null;
        }

        if (selectedDeck.ChildrenDeckCount <= 0)
        {
            var resolution = await _selectionResolver.ResolveAsync(selectedDeck.DeckId);
            if (resolution.IsSuccess)
            {
                WriteSelection(resolution.Selection!);
                return resolution.Selection;
            }

            if (resolution.Status == JitenSelectionStatus.ParentHasChildren)
            {
                _console.MarkupLine(
                    "[yellow]This deck has subdecks and cannot be linked directly as a single work.[/]");
                return await SelectSubdeckAsync(selectedDeck);
            }

            _console.MarkupLine(
                $"[red]{Markup.Escape(resolution.ErrorMessage ?? "Could not verify deck.")}[/]");
            return null;
        }

        return await SelectSubdeckAsync(selectedDeck);
    }

    public static IReadOnlyList<string> GetSubdeckPromptChoices() =>
        ["Choose a subdeck", "Cancel"];

    private async Task<JitenMediaSelection?> SelectSubdeckAsync(
        JitenDeckDTO selectedDeck)
    {
        var choices = GetSubdeckPromptChoices();
        var subtitle = selectedDeck.ChildrenDeckCount > 0
            ? $"{selectedDeck.ChildrenDeckCount} subdecks."
            : "subdecks.";

        var action = _console.Prompt(
            new SelectionPrompt<string>()
                .Title(
                    $"[green]{Markup.Escape(GetTitle(selectedDeck))}[/] contains {subtitle}")
                .AddChoices(choices));

        if (action == "Cancel")
        {
            return null;
        }

        JitenDeckDetailDTO? detail;

        try
        {
            detail = await _jitenApiClient.GetDeckDetailAsync(selectedDeck.DeckId);
        }
        catch (HttpRequestException exception)
        {
            _console.MarkupLine(
                $"[red]Could not load subdecks:[/] {Markup.Escape(exception.Message)}");
            return null;
        }

        if (detail is null || detail.SubDecks.Count == 0)
        {
            _console.MarkupLine("[yellow]No subdecks were returned by Jiten.[/]");
            return null;
        }

        var selectedSubdeck = SelectDeck(
            detail.SubDecks,
            "Select a [green]Jiten subdeck[/]:");

        if (selectedSubdeck is null)
        {
            return null;
        }

        var resolution = await _selectionResolver.ResolveAsync(
            selectedDeck.DeckId,
            selectedSubdeck.DeckId);

        if (!resolution.IsSuccess)
        {
            _console.MarkupLine(
                $"[red]{Markup.Escape(resolution.ErrorMessage ?? "Could not verify subdeck.")}[/]");
            return null;
        }

        WriteSelection(resolution.Selection!);
        return resolution.Selection;
    }

    private JitenDeckDTO? SelectDeck(
        IEnumerable<JitenDeckDTO> decks,
        string promptTitle)
    {
        var options = decks
            .Select(deck => new DeckOption(deck))
            .Append(new DeckOption(null))
            .ToList();

        return _console.Prompt(
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

    private void WriteSelection(JitenMediaSelection selection)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Title("[green]Jiten selection[/]")
            .AddColumn("Field")
            .AddColumn("Value");

        table.AddRow("Title", Markup.Escape(selection.DisplayTitle));
        table.AddRow("Deck ID", selection.DeckId.ToString());
        table.AddRow("Subdeck ID", selection.SubdeckId?.ToString() ?? "—");
        table.AddRow(
            "Link type",
            selection.IsSubdeck ? "Specific subdeck" : "Entire deck");
        table.AddRow("Characters", selection.CharacterCount.ToString("N0"));
        var coverLabel = selection.CoverEvidence switch
        {
            JitenCoverEvidence.Specific => "Volume cover",
            JitenCoverEvidence.ParentFallback => "Series cover fallback",
            _ => "No cover"
        };
        table.AddRow("Cover", coverLabel);

        _console.Write(table);
    }

    private sealed record DeckOption(JitenDeckDTO? Deck);
}
