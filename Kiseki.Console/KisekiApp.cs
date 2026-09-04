using Kiseki.Console.Screens;
using Spectre.Console;

namespace Kiseki.Console;

public sealed class KisekiApp
{
    private readonly AddMediaScreen _addMediaScreen;
    private readonly LibraryScreen _libraryScreen;

    public KisekiApp(
        AddMediaScreen addMediaScreen,
        LibraryScreen libraryScreen)
    {
        _addMediaScreen = addMediaScreen;
        _libraryScreen = libraryScreen;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an [green]option[/]:")
                    .AddChoices(
                        "Add media",
                        "View library",
                        "Load from Ttsu folder",
                        "Exit"));

            switch (choice)
            {
                case "Add media":
                    await _addMediaScreen.ShowAsync();
                    break;

                case "View library":
                    await _libraryScreen.ShowAsync();
                    break;

                case "Load from Ttsu folder":
                    await _addMediaScreen.ImportFromTtsuAsync();
                    break;

                case "Exit":
                    AnsiConsole.MarkupLine("[blue]Exiting...[/]");
                    return;
            }
        }
    }
}
