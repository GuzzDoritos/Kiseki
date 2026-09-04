using Kiseki.Console;
using Kiseki.Console.Screens;
using Kiseki.Core;
using Kiseki.Core.Services;
using System.Text;
using Spectre.Console;

System.Console.OutputEncoding = Encoding.UTF8;

await using var context = new ImmersionDbContext();
var backupPath = await DatabaseInitializer.MigrateAsync(context);

if (backupPath is not null)
{
    AnsiConsole.MarkupLine(
        $"[grey]Database migrated. Backup: {Markup.Escape(backupPath)}[/]");
}

IJitenApiClient jitenApiClient = new JitenApiClient();
var ttsuDataLoader = new TtsuDataLoader();
var jitenLinkScreen = new JitenLinkScreen(jitenApiClient);
var addMediaScreen = new AddMediaScreen(context, ttsuDataLoader, jitenLinkScreen);
var libraryScreen = new LibraryScreen(context, jitenLinkScreen);
var app = new KisekiApp(addMediaScreen, libraryScreen);

await app.RunAsync();
