using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Library;

public sealed class LinkJitenModel(
    ImmersionDbContext dbContext,
    IJitenApiClient jitenApiClient) : PageModel
{
    public Guid WorkId { get; private set; }
    public string WorkTitle { get; private set; } = string.Empty;
    public MediaType WorkMediaType { get; private set; }
    public bool HasExistingLink { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Query { get; set; }

    [BindProperty]
    public LinkJitenInput Input { get; set; } = new();

    public IReadOnlyList<JitenMediaSelection> Results { get; private set; } = [];
    public JitenMediaSelection? SelectedParent { get; private set; }
    public IReadOnlyList<JitenMediaSelection> Subdecks { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await LoadWorkAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(Query))
        {
            Query = WorkTitle;
            return Page();
        }

        return await SearchLoadedWorkAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetSearchAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await LoadWorkAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return await SearchLoadedWorkAsync(cancellationToken);
    }

    private async Task<IActionResult> SearchLoadedWorkAsync(
        CancellationToken cancellationToken)
    {
        if (!CanLinkWork())
        {
            return Page();
        }

        Query = Query?.Trim();
        if (string.IsNullOrWhiteSpace(Query))
        {
            ModelState.AddModelError(nameof(Query), "Enter a title to search Jiten.");
            return Page();
        }

        try
        {
            var decks = await jitenApiClient.SearchBooksAsync(Query, cancellationToken);
            Results = decks.Select(JitenMediaSelection.FromDeck).ToList();

            if (Results.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "Jiten did not return any matching books.");
            }
        }
        catch (Exception exception) when (IsDisplayableJitenFailure(exception, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, JitenFailureMessage(exception));
        }

        return Page();
    }

    public async Task<IActionResult> OnGetSubdecksAsync(
        Guid id,
        int deckId,
        CancellationToken cancellationToken)
    {
        if (!await LoadWorkAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (!CanLinkWork())
        {
            return Page();
        }

        if (deckId <= 0)
        {
            ModelState.AddModelError(string.Empty, "The selected Jiten deck is invalid.");
            return Page();
        }

        try
        {
            var detail = await jitenApiClient.GetDeckDetailAsync(deckId, cancellationToken);
            if (!TryPopulateDetail(detail, deckId))
            {
                ModelState.AddModelError(string.Empty, "Jiten could not verify the selected deck.");
            }
            else if (Subdecks.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "Jiten did not return any subdecks for this title.");
            }
        }
        catch (Exception exception) when (IsDisplayableJitenFailure(exception, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, JitenFailureMessage(exception));
        }

        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var work = await dbContext.MediaWorks
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return NotFound();
        }

        PopulateWork(work);
        Query = work.Title;

        if (!CanLinkWork())
        {
            return Page();
        }

        if (!Enum.IsDefined(Input.TitleChoice))
        {
            ModelState.AddModelError(nameof(Input.TitleChoice), "Choose a valid title option.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var detail = await jitenApiClient.GetDeckDetailAsync(
                Input.ParentDeckId,
                cancellationToken);

            if (!TryPopulateDetail(detail, Input.ParentDeckId) || SelectedParent is null)
            {
                ModelState.AddModelError(string.Empty, "Jiten could not verify the selected deck.");
                return Page();
            }

            var selection = Input.SubdeckId is int subdeckId
                ? Subdecks.SingleOrDefault(item => item.SubdeckId == subdeckId)
                : SelectedParent;

            if (selection is null)
            {
                ModelState.AddModelError(string.Empty, "The selected Jiten subdeck no longer exists.");
                return Page();
            }

            if (!selection.HasTitle(Input.TitleChoice))
            {
                ModelState.AddModelError(
                    nameof(Input.TitleChoice),
                    "The selected Jiten title is not available for this deck.");
                return Page();
            }

            selection.ApplyTo(work, Input.TitleChoice);
            await dbContext.SaveChangesAsync(cancellationToken);

            TempData["LibraryNotice"] = $"Linked “{work.Title}” to Jiten.";
            return RedirectToPage("/Library/Details", new { id = work.Id });
        }
        catch (Exception exception) when (IsDisplayableJitenFailure(exception, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, JitenFailureMessage(exception));
            return Page();
        }
    }

    private async Task<bool> LoadWorkAsync(Guid id, CancellationToken cancellationToken)
    {
        var work = await dbContext.MediaWorks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return false;
        }

        PopulateWork(work);
        return true;
    }

    private void PopulateWork(MediaWork work)
    {
        WorkId = work.Id;
        WorkTitle = work.Title;
        WorkMediaType = work.MediaType;
        HasExistingLink = work.HasJitenLink;
    }

    private bool CanLinkWork()
    {
        if (WorkMediaType == MediaType.Book)
        {
            return true;
        }

        ModelState.AddModelError(string.Empty, "Jiten linking currently supports books only.");
        return false;
    }

    private bool TryPopulateDetail(JitenDeckDetailDTO? detail, int requestedDeckId)
    {
        if (detail is null)
        {
            return false;
        }

        var parent = new[] { detail.MainDeck, detail.ParentDeck }
            .FirstOrDefault(deck => deck?.DeckId == requestedDeckId);

        if (parent is null)
        {
            return false;
        }

        SelectedParent = JitenMediaSelection.FromDeck(parent);
        Subdecks = detail.SubDecks
            .Where(deck => deck.DeckId > 0)
            .Select(deck => JitenMediaSelection.FromSubdeck(parent, deck))
            .ToList();
        return true;
    }

    private static bool IsDisplayableJitenFailure(
        Exception exception,
        CancellationToken requestCancellationToken)
    {
        return exception is HttpRequestException or JsonException or NotSupportedException ||
               exception is OperationCanceledException && !requestCancellationToken.IsCancellationRequested;
    }

    private static string JitenFailureMessage(Exception exception)
    {
        return exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                "Jiten's request limit was reached. Wait a moment and try again.",
            OperationCanceledException => "Jiten took too long to respond. Try again.",
            JsonException or NotSupportedException =>
                "Jiten returned metadata that Kiseki could not read.",
            _ => "Kiseki could not reach Jiten. Check your connection and try again."
        };
    }

    public sealed class LinkJitenInput
    {
        [Range(1, int.MaxValue)]
        public int ParentDeckId { get; set; }

        [Range(1, int.MaxValue)]
        public int? SubdeckId { get; set; }

        public JitenTitleChoice TitleChoice { get; set; } = JitenTitleChoice.KeepCurrent;
    }
}
