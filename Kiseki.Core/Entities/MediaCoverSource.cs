namespace Kiseki.Core.Entities;

public enum MediaCoverSource
{
    None = 0,
    LegacyUnknown = 1,
    JitenSpecific = 2,
    JitenParentFallback = 3,
    UserOverride = 4,
    GoogleBooks = 5,
    OpenLibrary = 6
}

