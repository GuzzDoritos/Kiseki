using Core;
using Core.DTOs;
using Core.Entities;
using Core.Services;
using Spectre.Console;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

Console.OutputEncoding = Encoding.UTF8;

var context = new ImmersionDbContext();

List<TtsuBookContainer> ttsuBooks = await TtsuDataLoader.LoadTtsuData();

var choice = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("Select an [green]option[/]:")
        .AddChoices("Add Media", "Load from Ttsu folder", "Exit"));

while (choice != "Exit")
{
    if (choice == "Add Media")
    {
        var mediaChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a [green]media type[/]:")
                .AddChoices("Book", "Anime", "Go back"));
        if (mediaChoice == "Book")
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
                        var log = new ImmersionLog();
                        log.CharactersRead = e.CharactersRead;
                        log.TimeSpentMinutes = e.ReadingTime;
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
                    var book = JitenApiClient.GetMediaFromQueryAsync(query).Result;
                    AnsiConsole.MarkupLine($"[green]Querying...[/]");
                    AnsiConsole.MarkupLine($"Hello, [blue]{book.DeckId} {book.OriginalTitle} {book.CharacterCount}[/]!");
                }
            }
        }
    }
    choice = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("Select an [green]option[/]:")
        .AddChoices("Add Media", "Load from Ttsu folder", "Exit"));
}

AnsiConsole.MarkupLine($"[blue]Exiting...[/]");