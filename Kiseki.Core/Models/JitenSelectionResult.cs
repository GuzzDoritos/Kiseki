namespace Kiseki.Core.Models;

public enum JitenSelectionStatus
{
    Success,
    InvalidDeckId,
    DeckNotFound,
    MismatchedParent,
    ParentHasChildren,
    SubdeckNotFound
}

public sealed record JitenSelectionResult(
    JitenSelectionStatus Status,
    JitenMediaSelection? Selection = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == JitenSelectionStatus.Success && Selection is not null;

    public static JitenSelectionResult Succeeded(JitenMediaSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new JitenSelectionResult(JitenSelectionStatus.Success, Selection: selection);
    }

    public static JitenSelectionResult Failed(JitenSelectionStatus status, string errorMessage)
    {
        if (status == JitenSelectionStatus.Success)
        {
            throw new ArgumentException("Cannot create a failure result with status Success.", nameof(status));
        }

        return new JitenSelectionResult(status, ErrorMessage: errorMessage);
    }
}

