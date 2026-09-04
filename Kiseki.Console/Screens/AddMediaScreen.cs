using Kiseki.Console.Display;
using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Spectre.Console;

namespace Kiseki.Console.Screens;

public sealed class AddMediaScreen
{
    private const string TtsuDataPathEnvironmentVariable = "KISEKI_TTSU_DATA_PATH";
    private const string TtsuChoice = "Add book from Ttsu folder (with stats)";
    private const string JitenChoice = "Add book from Jiten (no reading stats)";

    private readonly ImmersionDbContext _context;
    private readonly TtsuDataLoader _ttsuDataLoader;
    private readonly JitenLinkScreen _jitenLinkScreen;

    public AddMediaScreen(
        ImmersionDbContext context,
        TtsuDataLoader ttsuDataLoader,
        JitenLinkScreen jitenLinkScreen)
    {
        _context = context;
        _ttsuDataLoader = ttsuDataLoader;
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
        var pathPrompt = new TextPrompt<string>("Enter the [green]TTSU data folder[/] path:")
            .Validate(path => !string.IsNullOrWhiteSpace(path)
                ? ValidationResult.Success()
                : ValidationResult.Error("A folder path is required."));
        var configuredPath = Environment.GetEnvironmentVariable(TtsuDataPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            pathPrompt.DefaultValue(configuredPath);
        }

        var rootPath = AnsiConsole.Prompt(pathPrompt);
        IReadOnlyList<TtsuBookContainer> ttsuBooks;
        try
        {
            ttsuBooks = await _ttsuDataLoader.LoadDirectoryAsync(rootPath);
        }
        catch (Exception exception) when (exception is
            DirectoryNotFoundException or
            UnauthorizedAccessException or
            IOException or
            InvalidDataException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return;
        }

        if (ttsuBooks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No books were found in the Ttsu folder.[/]");
            return;
        }

        var bookChoice = AnsiConsole.Prompt(
            new SelectionPrompt<TtsuBookContainer>()
                .Title("Select a [green]book[/]:")
                .UseConverter(book => Markup.Escape(book.Title))
                .AddChoices(ttsuBooks)
                .EnableSearch());

        var newBook = TtsuBookImporter.CreateMediaWork(bookChoice);

        _context.MediaWorks.Add(newBook);
        await _context.SaveChangesAsync();

        AnsiConsole.MarkupLine("[green]Book imported successfully.[/]");
        MediaWorkTable.Write([newBook], "Imported book");
    }

    private async Task PickBookSourceAsync()
    {
        var choices = new List<string>
        {
            TtsuChoice,
            JitenChoice,
            "Cancel"
        };

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
