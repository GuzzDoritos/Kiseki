using Core;
using Core.DTOs;
using Core.Entities;
using Core.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var context = new ImmersionDbContext();

List<TtsuBookContainer> ttsuBooks = await TtsuDataLoader.LoadTtsuData();

SelectionPrompt<string> homePrompt = new SelectionPrompt<string>()
        .Title("Select an [green]option[/]:")
        .AddChoices("Add Media", "Load from Ttsu folder", "Exit");

SelectionPrompt<string> pickMediaTypePrompt = new SelectionPrompt<string>()
                .Title("Select a [green]media type[/]:")
                .AddChoices("Book", "Anime", "Go back");

var choice = AnsiConsole.Prompt(homePrompt);

async Task AddMediaRoutine()
{
    var mediaChoice = AnsiConsole.Prompt(pickMediaTypePrompt);
        if (mediaChoice == "Book")
        {
            await PickBook();
        }
}

async Task PickBook()
{
    if (ttsuBooks.Count > 0)
            {
                string userChoice1 = "Add book from ttsu folder (with stats)";
                string userChoice2 = "Add book from Jiten (no stats, can be linked later)";
                var userPick = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select an option:")
                        .AddChoices(userChoice1, userChoice2, "Cancel"));
                if (userPick == userChoice1)
                {
                    var bookChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<TtsuBookContainer>()
                            .Title("Select a [green]book[/]:")
                            .UseConverter(b => b.Title)
                            .AddChoices(ttsuBooks)
                            .EnableSearch());
                    AnsiConsole.MarkupLine($"[blue]Title:[/] {bookChoice.Title}");
                    AnsiConsole.MarkupLine($"[blue]Title:[/] {bookChoice}");
                    
                    var newBook = new MediaWork(bookChoice.Title);
                    foreach(var e in bookChoice.Entries)
                    {
                        var log = new ImmersionLog
                        {
                            CharactersRead = e.CharactersRead,
                            TimeSpentMinutes = e.ReadingTime
                        };
                        newBook.Logs.Add(log);
                        context.Add(log);    
                    }

                    context.Add(newBook);
                    context.SaveChanges();

                    var test = context.MediaWorks.Where(b => b.Id == newBook.Id).First();
                    var table = new Table();
                    table.AddColumn("Title");
                    table.AddColumn("Total Characters");
                    table.AddRow(test.Title, test.TotalCharacters.ToString());

                    AnsiConsole.Write(table);

                }
                if (userPick == userChoice2)
                {
                    string query = AnsiConsole.Ask<string>("Search query for [green]book[/].");
                    AnsiConsole.MarkupLine($"[green]Querying...[/]");
                    var book = await JitenApiClient.GetMediaFromQueryAsync(query);

                    if (book is null) 
                    {
                        AnsiConsole.MarkupLine("[red]No book found.[/]");
                        return;
                    }

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .BorderColor(Color.Blue)
                        .Title("[green]Jiten result[/]");

                    table.AddColumn("Field");
                    table.AddColumn("Value");

                    table.AddRow("Deck ID", book.DeckId.ToString());
                    table.AddRow("Original title", Markup.Escape(book.OriginalTitle));
                    table.AddRow("Romaji title", Markup.Escape(book.RomajiTitle));
                    table.AddRow("English title", Markup.Escape(book.EnglishTitle?? "None"));
                    table.AddRow("Release date", book.ReleaseDate?.ToString("yyyy-MM-dd") ?? "Unknown");
                    table.AddRow("Characters", book.CharacterCount.ToString("N0"));

                    AnsiConsole.Write(table);

                    if (AnsiConsole.Confirm("Use this result?"))
                    {
                        MediaWork mediaWork = new(book.OriginalTitle, book.DeckId, book.CharacterCount);

                        context.MediaWorks.Add(mediaWork);
                        await context.SaveChangesAsync();

                        var mediaWorks = await context.MediaWorks.ToListAsync();

                        var tableMW = new Table()
                                    .Border(TableBorder.Rounded)
                                    .AddColumn("ID")
                                    .AddColumn("Title")
                                    .AddColumn("Characters");

                        foreach (var m in mediaWorks)
                        {
                            tableMW.AddRow(
                                m.Id.ToString(),
                                Markup.Escape(m.Title),
                                m.TotalCharacters.ToString("N0"));
                        }

                        AnsiConsole.Write(tableMW);
            }
                }
            }
}

// main loop

while (choice != "Exit")
{
    if (choice == "Add Media")
    {
        await AddMediaRoutine();
    }
    choice = AnsiConsole.Prompt(homePrompt);
}

AnsiConsole.MarkupLine($"[blue]Exiting...[/]");