using Core;
using Core.DTOs;
using Core.Entities;
using ImmersionTracker.ConsoleApp.Display;
using Spectre.Console;

namespace ImmersionTracker.ConsoleApp.Screens;

public sealed class AddMediaScreen
{
    private const string TtsuChoice = "Add book from Ttsu folder (with stats)";
    private const string JitenChoice = "Add book from Jiten (no reading stats)";

    private readonly ImmersionDbContext _context;
    private readonly List<TtsuBookContainer> _ttsuBooks;
    private readonly JitenLinkScreen _jitenLinkScreen;

    public AddMediaScreen(
        ImmersionDbContext context,
        List<TtsuBookContainer> ttsuBooks,
        JitenLinkScreen jitenLinkScreen)
    {
        _context = context;
        _ttsuBooks = ttsuBooks;
        _jitenLinkScreen = jitenLinkScreen;
    }

    public async Task ShowAsync()
    {
        var mediaType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a [green]media type[/]:")
                .AddChoices("Book", "Anime", "Game", "Go back"));

        if (mediaType == "Book")
        {
            await PickBookSourceAsync();
        }
        else if (mediaType != "Go back")
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{mediaType} import has not been implemented yet.[/]");
        }
    }

    public async Task ImportFromTtsuAsync()
    {
        if (_ttsuBooks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No books were found in the Ttsu folder.[/]");
            return;
        }

        var bookChoice = AnsiConsole.Prompt(
            new SelectionPrompt<TtsuBookContainer>()
                .Title("Select a [green]book[/]:")
                .UseConverter(book => Markup.Escape(book.Title))
                .AddChoices(_ttsuBooks)
                .EnableSearch());

        var newBook = new MediaWork(bookChoice.Title, mediaType: MediaType.Book);

        foreach (var entry in bookChoice.Entries)
        {
            newBook.Logs.Add(new ImmersionLog
            {
                CharactersRead = entry.CharactersRead,
                TimeSpentMinutes = entry.ReadingTime
            });
        }

        _context.MediaWorks.Add(newBook);
        await _context.SaveChangesAsync();

        AnsiConsole.MarkupLine("[green]Book imported successfully.[/]");
        MediaWorkTable.Write([newBook], "Imported book");
    }

    private async Task PickBookSourceAsync()
    {
        var choices = new List<string>();

        if (_ttsuBooks.Count > 0)
        {
            choices.Add(TtsuChoice);
        }

        choices.Add(JitenChoice);
        choices.Add("Cancel");

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select an option:")
                .AddChoices(choices));

        switch (choice)
        {
            case TtsuChoice:
                await ImportFromTtsuAsync();
                break;

            case JitenChoice:
                await ImportFromJitenAsync();
                break;
        }
    }

    private async Task ImportFromJitenAsync()
    {
        var selection = await _jitenLinkScreen.SelectBookAsync();

        if (selection is null || !AnsiConsole.Confirm("Add this book to your library?"))
        {
            return;
        }

        var mediaWork = new MediaWork(selection.Title, mediaType: MediaType.Book);
        selection.ApplyTo(mediaWork);

        _context.MediaWorks.Add(mediaWork);
        await _context.SaveChangesAsync();

        AnsiConsole.MarkupLine("[green]Book added successfully.[/]");
        MediaWorkTable.Write([mediaWork], "Added book");
    }
}
