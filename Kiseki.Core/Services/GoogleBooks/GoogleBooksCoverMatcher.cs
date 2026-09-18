using System.Text;
using System.Text.RegularExpressions;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Core.Services.GoogleBooks;

public sealed record GoogleCoverLookupContext(
    string RawTtsuTitle,
    ParsedMediaTitle ParsedTtsuTitle,
    StructuredVolume? TargetVolume,
    string? JitenParentOriginalTitle,
    string? JitenParentRomajiTitle,
    string? JitenParentEnglishTitle,
    string? JitenSubdeckTitle,
    StructuredVolume? JitenSubdeckVolume,
    string? JitenDeckOriginalTitle,
    string? JitenDeckRomajiTitle,
    string? JitenDeckEnglishTitle,
    bool IsSubdeck,
    bool IsStandalone,
    int DeckId,
    int? SubdeckId,
    bool IsExactIdentity = false);

public sealed record GoogleBooksQueryResultGroup(
    string Query,
    IReadOnlyList<GoogleBooksVolumeDto> Items);

public sealed record GoogleBooksMatchedVolume(
    GoogleBooksVolumeDto Volume,
    GoogleBooksIdentityProof Proof,
    IReadOnlyList<string> Evidence,
    string? NormalizedIsbn = null);

public interface IGoogleBooksCoverMatcher
{
    IReadOnlyList<string> GenerateQueries(GoogleCoverLookupContext context);

    GoogleBooksMatchedVolume? FindUniqueMatchingVolume(
        GoogleCoverLookupContext context,
        IReadOnlyList<GoogleBooksQueryResultGroup> queryGroups,
        out GoogleBooksCoverMatchResult? failureResult);

    GoogleBooksVolumeDto? FindUniqueMatchingVolume(
        GoogleCoverLookupContext context,
        IEnumerable<GoogleBooksVolumeDto> candidates,
        out GoogleBooksCoverMatchResult? failureResult);

    GoogleBooksMatchedVolume? VerifyFreshVolume(
        GoogleCoverLookupContext context,
        GoogleBooksVolumeDto freshVolume,
        GoogleBooksMatchedVolume searchMatched,
        out GoogleBooksCoverMatchResult? failureResult)
    {
        failureResult = null;
        return searchMatched with { Volume = freshVolume };
    }
}

public sealed class GoogleBooksCoverMatcher : IGoogleBooksCoverMatcher
{
    private const int MaxVolumeIdLength = 128;
    private static readonly Regex OmnibusRegex = new(
        @"(?:\d+[\-~～]\d+)|合本|総集編|全\d+巻",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AdaptationNoiseRegex = new(
        @"(?:コミック|コミックス|manga|漫画)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShortStoryOrSpecialNoiseRegex = new(
        @"(?:短編集|短篇集|外伝|アンソロジー|ファンブック|ガイドブック|画集|特別編|前編|後編|上巻|中巻|下巻)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SplitEditionRegex = new(
        @"[\(（][上下中][\)）]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<string> GenerateQueries(GoogleCoverLookupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Check for volume conflicts between TTSU and Jiten subdeck
        if (context.TargetVolume is not null && context.JitenSubdeckVolume is not null &&
            context.TargetVolume.ConflictsWith(context.JitenSubdeckVolume))
        {
            return [];
        }

        var effectiveVolume = context.TargetVolume ?? context.JitenSubdeckVolume;

        // A subdeck without an explicit volume identity must never degrade into a
        // broad series-only Google Books query.
        if (context.IsSubdeck && effectiveVolume is null)
        {
            return [];
        }

        // Special volumes are strictly excluded from automatic search
        if (effectiveVolume is not null && (effectiveVolume.IsSpecial || effectiveVolume.Kind != VolumeKind.Standard))
        {
            return [];
        }

        var baseTitle = ResolveBestJapaneseBaseTitle(context, effectiveVolume);
        if (string.IsNullOrWhiteSpace(baseTitle))
        {
            return [];
        }

        var queries = new List<string>();

        if (effectiveVolume is not null && effectiveVolume.Number.HasValue)
        {
            var volumeNumber = (int)effectiveVolume.Number.Value;
            var q1 = $"{baseTitle} {volumeNumber}巻";
            var q2 = $"intitle:{baseTitle} {volumeNumber}";

            queries.Add(q1);
            if (!string.Equals(q1, q2, StringComparison.Ordinal))
            {
                queries.Add(q2);
            }
        }
        else if (context.IsStandalone || effectiveVolume is null)
        {
            queries.Add(baseTitle);
        }

        return queries.Distinct(StringComparer.Ordinal).Take(2).ToList();
    }

    public GoogleBooksVolumeDto? FindUniqueMatchingVolume(
        GoogleCoverLookupContext context,
        IEnumerable<GoogleBooksVolumeDto> candidates,
        out GoogleBooksCoverMatchResult? failureResult)
    {
        var list = candidates.ToList();
        var group = new GoogleBooksQueryResultGroup("default", list);
        var matched = FindUniqueMatchingVolume(context, [group], out failureResult);
        return matched?.Volume;
    }

    public GoogleBooksMatchedVolume? FindUniqueMatchingVolume(
        GoogleCoverLookupContext context,
        IReadOnlyList<GoogleBooksQueryResultGroup> queryGroups,
        out GoogleBooksCoverMatchResult? failureResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queryGroups);

        failureResult = null;

        // Check for volume conflicts
        if (context.TargetVolume is not null && context.JitenSubdeckVolume is not null &&
            context.TargetVolume.ConflictsWith(context.JitenSubdeckVolume))
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "Conflicting volume markers between TTSU and Jiten subdeck.");
            return null;
        }

        var effectiveVolume = context.TargetVolume ?? context.JitenSubdeckVolume;

        if (context.IsSubdeck && effectiveVolume is null)
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "The selected Jiten subdeck has no explicit volume identity.");
            return null;
        }

        if (effectiveVolume is not null && (effectiveVolume.IsSpecial || effectiveVolume.Kind != VolumeKind.Standard))
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "Special/fractional volumes do not support automatic Google Books matching.");
            return null;
        }

        var trustedVariants = GetTrustedBaseTitleVariants(context)
            .Select(NormalizeTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.Ordinal);

        var trustedJapaneseVariants = GetTrustedJapaneseBaseTitleVariants(context)
            .Select(NormalizeTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.Ordinal);

        if (trustedVariants.Count == 0)
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "No trusted Japanese base title variants available.");
            return null;
        }

        // Deduplicate candidates preserving all items
        var allCandidates = queryGroups
            .SelectMany(g => g.Items)
            .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ----------------------------------------------------
        // Path 1: Strict Explicit-Volume Matching (Preferred)
        // ----------------------------------------------------
        var eligibleStrictCandidates = new List<(GoogleBooksVolumeDto Candidate, string PreferredImageLink)>();

        foreach (var cand in allCandidates)
        {
            if (!IsValidVolumeId(cand.Id)) continue;
            var info = cand.VolumeInfo;
            if (info is null) continue;

            // Gate 1: Japanese language exactly
            if (!string.Equals(info.Language?.Trim(), "ja", StringComparison.OrdinalIgnoreCase))
                continue;

            // Gate 2: Usable image link
            var imageLink = info.ImageLinks?.GetPreferredImageLink();
            if (string.IsNullOrWhiteSpace(imageLink))
                continue;

            var fullTitle = string.IsNullOrWhiteSpace(info.Subtitle)
                ? info.Title ?? string.Empty
                : $"{info.Title} {info.Subtitle}";

            if (OmnibusRegex.IsMatch(fullTitle))
                continue;

            var parsedCand = MediaTitleParser.ParseTitle(info.Title ?? string.Empty);
            var candVolume = parsedCand.Volume ?? MediaTitleParser.ParseVolumeMarker(info.Title);
            StructuredVolume? subtitleVolume = null;

            if (!string.IsNullOrWhiteSpace(info.Subtitle))
            {
                var parsedSub = MediaTitleParser.ParseTitle(info.Subtitle);
                subtitleVolume = parsedSub.Volume ?? MediaTitleParser.ParseVolumeMarker(info.Subtitle);
            }

            if (candVolume is not null && subtitleVolume is not null &&
                candVolume.ConflictsWith(subtitleVolume))
            {
                continue;
            }

            candVolume ??= subtitleVolume;

            if (effectiveVolume is not null)
            {
                // Must have exact standard volume matching number
                if (candVolume is null ||
                    candVolume.Kind != VolumeKind.Standard ||
                    candVolume.IsSpecial ||
                    candVolume.Number != effectiveVolume.Number)
                {
                    continue;
                }
            }
            else
            {
                if (candVolume is not null)
                    continue;
            }

            // Gate 4: Title match
            var candBase = NormalizeTitle(parsedCand.BaseTitle);
            if (!trustedVariants.Contains(candBase))
                continue;

            // Gate 5: Adaptation noise
            if (AdaptationNoiseRegex.IsMatch(parsedCand.BaseTitle) &&
                !trustedVariants.Any(t => AdaptationNoiseRegex.IsMatch(t)))
            {
                continue;
            }

            eligibleStrictCandidates.Add((cand, imageLink));
        }

        if (eligibleStrictCandidates.Count > 0)
        {
            if (eligibleStrictCandidates.Count > 1)
            {
                var collapsed = TryCollapseEquivalentEditions(eligibleStrictCandidates);
                if (collapsed is null)
                {
                    var distinctEditions = GetDistinctCandidateEditions(eligibleStrictCandidates);
                    failureResult = GoogleBooksCoverMatchResult.CreateAmbiguous(
                        warning: $"Ambiguous match: {eligibleStrictCandidates.Count} Google Books volumes satisfied all criteria.",
                        candidateEditions: distinctEditions);
                    return null;
                }

                var matchedIsbn = GetNormalizedIsbn(collapsed.Value.Candidate.VolumeInfo);
                return new GoogleBooksMatchedVolume(
                    collapsed.Value.Candidate,
                    GoogleBooksIdentityProof.ExplicitVolume,
                    ["Google title matched the trusted Japanese base title and volume explicitly."],
                    matchedIsbn);
            }

            var singleStrict = eligibleStrictCandidates[0].Candidate;
            var singleStrictIsbn = GetNormalizedIsbn(singleStrict.VolumeInfo);
            return new GoogleBooksMatchedVolume(
                singleStrict,
                GoogleBooksIdentityProof.ExplicitVolume,
                ["Google title matched the trusted Japanese base title and volume explicitly."],
                singleStrictIsbn);
        }

        // ----------------------------------------------------
        // Path 2: Markerless Strong-Inference Fallback
        // ----------------------------------------------------
        // Consider markerless record ONLY when all conditions pass:
        // 1. Trusted context has exact standard integer volume and passed IsExactIdentity
        if (!context.IsExactIdentity ||
            effectiveVolume is null ||
            effectiveVolume.Kind != VolumeKind.Standard ||
            effectiveVolume.IsSpecial ||
            !effectiveVolume.Number.HasValue ||
            queryGroups.Count == 0)
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "Google Books checked — no exact volume cover found");
            return null;
        }

        var foundExactTitleMarkerless = false;
        var eligibleMarkerlessCandidates = new List<(GoogleBooksVolumeDto Candidate, string Isbn)>();

        foreach (var cand in allCandidates)
        {
            if (!IsValidVolumeId(cand.Id)) continue;
            var info = cand.VolumeInfo;
            if (info is null) continue;

            // 2. Language == "ja"
            if (!string.Equals(info.Language?.Trim(), "ja", StringComparison.OrdinalIgnoreCase))
                continue;

            // 9. Usable image link
            var imageLink = info.ImageLinks?.GetPreferredImageLink();
            if (string.IsNullOrWhiteSpace(imageLink))
                continue;

            var fullTitle = string.IsNullOrWhiteSpace(info.Subtitle)
                ? info.Title ?? string.Empty
                : $"{info.Title} {info.Subtitle}";

            // 4. No omnibus, adaptation, short-story, or split-edition noise
            if (OmnibusRegex.IsMatch(fullTitle) ||
                AdaptationNoiseRegex.IsMatch(fullTitle) ||
                ShortStoryOrSpecialNoiseRegex.IsMatch(fullTitle) ||
                SplitEditionRegex.IsMatch(fullTitle))
            {
                continue;
            }

            // Check if title or subtitle has ANY volume marker
            var parsedCand = MediaTitleParser.ParseTitle(info.Title ?? string.Empty);
            var candVolume = parsedCand.Volume ?? MediaTitleParser.ParseVolumeMarker(info.Title);
            StructuredVolume? subtitleVolume = null;

            if (!string.IsNullOrWhiteSpace(info.Subtitle))
            {
                var parsedSub = MediaTitleParser.ParseTitle(info.Subtitle);
                subtitleVolume = parsedSub.Volume ?? MediaTitleParser.ParseVolumeMarker(info.Subtitle);
            }

            if (candVolume is not null || subtitleVolume is not null)
            {
                // Has explicit volume marker -> not markerless!
                continue;
            }

            // 3. Normalized base title exactly equals trusted Japanese base title.
            // Title must have no extra title material beyond the trusted base.
            var candBase = NormalizeTitle(parsedCand.BaseTitle);
            var rawTitleNormalized = NormalizeTitle(info.Title ?? string.Empty);
            if (candBase != rawTitleNormalized || !trustedJapaneseVariants.Contains(candBase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(info.Subtitle))
            {
                var subNorm = NormalizeTitle(info.Subtitle);
                if (subNorm.Length > 0 && !trustedJapaneseVariants.Contains(subNorm))
                {
                    continue;
                }
            }

            foundExactTitleMarkerless = true;

            // 5. Valid normalized ISBN with check digit validation
            var isbn = GetNormalizedIsbn(info);
            if (string.IsNullOrWhiteSpace(isbn))
            {
                continue;
            }

            // 6. Stable Google volume ID and normalized ISBN appear in EVERY query result group
            var appearsInAllGroups = queryGroups.All(group =>
                group.Items.Any(item =>
                    string.Equals(item.Id, cand.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetNormalizedIsbn(item.VolumeInfo), isbn, StringComparison.Ordinal)));

            if (!appearsInAllGroups)
            {
                continue;
            }

            eligibleMarkerlessCandidates.Add((cand, isbn));
        }

        // 7. Group equivalent records by normalized ISBN
        var groupedByIsbn = eligibleMarkerlessCandidates
            .GroupBy(c => c.Isbn, StringComparer.Ordinal)
            .ToList();

        if (groupedByIsbn.Count == 0)
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: foundExactTitleMarkerless
                    ? "Google Books checked — exact title found, but Google omitted the volume number and cross-query proof was insufficient"
                    : "Google Books checked — no exact volume cover found");
            return null;
        }

        if (groupedByIsbn.Count > 1)
        {
            failureResult = GoogleBooksCoverMatchResult.CreateAmbiguous(
                warning: "Google Books checked — multiple markerless editions remained ambiguous");
            return null;
        }

        // Exactly one markerless candidate group
        var bestCandidate = groupedByIsbn[0]
            .OrderByDescending(c => GetImageTierRank(c.Candidate.VolumeInfo?.ImageLinks))
            .ThenBy(c => c.Candidate.Id, StringComparer.Ordinal)
            .First();

        var volumeNumberDisplay = effectiveVolume.Number.Value;
        var inferredEvidence = new List<string>
        {
            "Google title matched the trusted Japanese base title exactly.",
            "Google omitted the volume marker.",
            $"The same ISBN/volume ID was the unique exact-base result in both volume-{volumeNumberDisplay} searches.",
            "Volume identity is strongly inferred from cross-query agreement."
        };

        return new GoogleBooksMatchedVolume(
            bestCandidate.Candidate,
            GoogleBooksIdentityProof.CrossQueryInferredVolume,
            inferredEvidence,
            bestCandidate.Isbn);
    }

    private static (GoogleBooksVolumeDto Candidate, string PreferredImageLink)? TryCollapseEquivalentEditions(
        List<(GoogleBooksVolumeDto Candidate, string PreferredImageLink)> candidates)
    {
        var isbns = new List<string>();
        foreach (var (cand, _) in candidates)
        {
            var isbn = GetNormalizedIsbn(cand.VolumeInfo);
            if (string.IsNullOrWhiteSpace(isbn))
            {
                return null;
            }
            isbns.Add(isbn);
        }

        var distinctIsbns = isbns.Distinct(StringComparer.Ordinal).ToList();
        if (distinctIsbns.Count != 1)
        {
            return null;
        }

        var firstImageKey = GetImageIdentityKey(candidates[0].PreferredImageLink);
        for (var i = 1; i < candidates.Count; i++)
        {
            var otherImageKey = GetImageIdentityKey(candidates[i].PreferredImageLink);
            if (!string.Equals(firstImageKey, otherImageKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var best = candidates
            .OrderByDescending(c => GetImageTierRank(c.Candidate.VolumeInfo?.ImageLinks))
            .ThenBy(c => c.Candidate.Id, StringComparer.Ordinal)
            .First();

        return best;
    }

    public static IReadOnlyList<GoogleBooksVolumeDto> GetDistinctCandidateEditions(
        List<(GoogleBooksVolumeDto Candidate, string PreferredImageLink)> candidates)
    {
        var groups = candidates
            .GroupBy(c =>
            {
                var isbn = GetNormalizedIsbn(c.Candidate.VolumeInfo);
                var imgKey = GetImageIdentityKey(c.PreferredImageLink);
                return (Key: isbn ?? c.Candidate.Id, Img: imgKey);
            })
            .Select(g => g.OrderByDescending(c => GetImageTierRank(c.Candidate.VolumeInfo?.ImageLinks)).First().Candidate)
            .OrderByDescending(c => GetImageTierRank(c.VolumeInfo?.ImageLinks))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(3)
            .ToList();

        return groups;
    }

    public static string? GetNormalizedIsbn(GoogleBooksVolumeInfoDto? info)
    {
        if (info?.IndustryIdentifiers is null || info.IndustryIdentifiers.Count == 0)
        {
            return null;
        }

        var isbn13 = info.IndustryIdentifiers
            .FirstOrDefault(i => string.Equals(i.Type, "ISBN_13", StringComparison.OrdinalIgnoreCase))
            ?.Identifier;
        var validated13 = IsbnValidator.NormalizeAndValidateIsbn(isbn13);
        if (!string.IsNullOrWhiteSpace(validated13))
        {
            return validated13;
        }

        var isbn10 = info.IndustryIdentifiers
            .FirstOrDefault(i => string.Equals(i.Type, "ISBN_10", StringComparison.OrdinalIgnoreCase))
            ?.Identifier;
        return IsbnValidator.NormalizeAndValidateIsbn(isbn10);
    }

    private static string GetImageIdentityKey(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(imageUrl.Trim(), UriKind.Absolute, out var uri))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var contentId = query["id"];
            if (!string.IsNullOrWhiteSpace(contentId))
            {
                return $"content_id:{contentId.Trim()}";
            }
            return $"{uri.Host}{uri.AbsolutePath}";
        }

        return imageUrl.Trim();
    }

    private static int GetImageTierRank(GoogleBooksImageLinksDto? links)
    {
        if (links is null) return 0;
        if (!string.IsNullOrWhiteSpace(links.ExtraLarge)) return 6;
        if (!string.IsNullOrWhiteSpace(links.Large)) return 5;
        if (!string.IsNullOrWhiteSpace(links.Medium)) return 4;
        if (!string.IsNullOrWhiteSpace(links.Small)) return 3;
        if (!string.IsNullOrWhiteSpace(links.Thumbnail)) return 2;
        if (!string.IsNullOrWhiteSpace(links.SmallThumbnail)) return 1;
        return 0;
    }

    private static bool IsValidVolumeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxVolumeIdLength &&
               trimmed.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~');
    }

    public static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // NFKC normalization
        var normalized = title.Normalize(NormalizationForm.FormKC);

        // Collapse whitespace
        var collapsed = Regex.Replace(normalized, @"\s+", " ").Trim();

        // Invariant lowercase
        var lower = collapsed.ToLowerInvariant();

        // Remove conservative punctuation noise
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (c is '!' or '?' or '！' or '？' or ':' or '：' or '-' or '―' or 'ー' or '~' or '～' or '·' or '・' or '_' or '/' or '・')
            {
                continue;
            }
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static string ResolveBestJapaneseBaseTitle(
        GoogleCoverLookupContext context,
        StructuredVolume? effectiveVolume)
    {
        if (context.IsSubdeck && !string.IsNullOrWhiteSpace(context.JitenParentOriginalTitle))
        {
            return context.JitenParentOriginalTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(context.JitenDeckOriginalTitle))
        {
            var jitenTitle = context.JitenDeckOriginalTitle.Trim();
            return effectiveVolume is null
                ? jitenTitle
                : MediaTitleParser.ParseTitle(jitenTitle).BaseTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(context.ParsedTtsuTitle.BaseTitle))
        {
            return context.ParsedTtsuTitle.BaseTitle.Trim();
        }

        return context.RawTtsuTitle.Trim();
    }

    private static IEnumerable<string> GetTrustedBaseTitleVariants(GoogleCoverLookupContext context)
    {
        var variants = new List<string>();

        if (context.IsSubdeck)
        {
            if (!string.IsNullOrWhiteSpace(context.JitenParentOriginalTitle))
                variants.Add(context.JitenParentOriginalTitle);
            if (!string.IsNullOrWhiteSpace(context.JitenParentRomajiTitle))
                variants.Add(context.JitenParentRomajiTitle);
            if (!string.IsNullOrWhiteSpace(context.JitenParentEnglishTitle))
                variants.Add(context.JitenParentEnglishTitle);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(context.JitenDeckOriginalTitle))
                variants.Add(RemoveTargetVolumeMarker(context.JitenDeckOriginalTitle, context.TargetVolume));
            if (!string.IsNullOrWhiteSpace(context.JitenDeckRomajiTitle))
                variants.Add(RemoveTargetVolumeMarker(context.JitenDeckRomajiTitle, context.TargetVolume));
            if (!string.IsNullOrWhiteSpace(context.JitenDeckEnglishTitle))
                variants.Add(RemoveTargetVolumeMarker(context.JitenDeckEnglishTitle, context.TargetVolume));
        }

        if (!string.IsNullOrWhiteSpace(context.ParsedTtsuTitle.BaseTitle))
        {
            variants.Add(context.ParsedTtsuTitle.BaseTitle);
        }

        return variants;
    }

    private static IEnumerable<string> GetTrustedJapaneseBaseTitleVariants(GoogleCoverLookupContext context)
    {
        var variants = new List<string>();

        if (context.IsSubdeck)
        {
            if (!string.IsNullOrWhiteSpace(context.JitenParentOriginalTitle))
                variants.Add(context.JitenParentOriginalTitle);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(context.JitenDeckOriginalTitle))
                variants.Add(RemoveTargetVolumeMarker(context.JitenDeckOriginalTitle, context.TargetVolume));
        }

        if (!string.IsNullOrWhiteSpace(context.ParsedTtsuTitle.BaseTitle))
        {
            variants.Add(context.ParsedTtsuTitle.BaseTitle);
        }

        return variants;
    }

    public GoogleBooksMatchedVolume? VerifyFreshVolume(
        GoogleCoverLookupContext context,
        GoogleBooksVolumeDto freshVolume,
        GoogleBooksMatchedVolume searchMatched,
        out GoogleBooksCoverMatchResult? failureResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(freshVolume);
        ArgumentNullException.ThrowIfNull(searchMatched);

        failureResult = null;

        if (!IsValidVolumeId(freshVolume.Id) ||
            !string.Equals(freshVolume.Id, searchMatched.Volume.Id, StringComparison.Ordinal))
        {
            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "Re-fetched Google Books volume ID did not match the reviewed volume.");
            return null;
        }

        if (searchMatched.Proof == GoogleBooksIdentityProof.ExplicitVolume)
        {
            var matched = FindUniqueMatchingVolume(context, [freshVolume], out failureResult);
            if (failureResult is not null || matched is null ||
                !string.Equals(matched.Id, freshVolume.Id, StringComparison.Ordinal))
            {
                failureResult ??= GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume did not satisfy explicit volume matching gates.");
                return null;
            }

            return searchMatched with { Volume = freshVolume };
        }

        if (searchMatched.Proof == GoogleBooksIdentityProof.CrossQueryInferredVolume)
        {
            var info = freshVolume.VolumeInfo;
            if (info is null)
            {
                failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume did not contain volume information.");
                return null;
            }

            // Language == "ja"
            if (!string.Equals(info.Language?.Trim(), "ja", StringComparison.OrdinalIgnoreCase))
            {
                failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume language is not Japanese.");
                return null;
            }

            // Usable image link
            var imageLink = info.ImageLinks?.GetPreferredImageLink();
            if (string.IsNullOrWhiteSpace(imageLink))
            {
                failureResult = GoogleBooksCoverMatchResult.CreateInvalidImage(
                    "Re-fetched volume does not provide a usable image link.");
                return null;
            }

            var fullTitle = string.IsNullOrWhiteSpace(info.Subtitle)
                ? info.Title ?? string.Empty
                : $"{info.Title} {info.Subtitle}";

            // No omnibus, adaptation, short-story, or split-edition noise
            if (OmnibusRegex.IsMatch(fullTitle) ||
                AdaptationNoiseRegex.IsMatch(fullTitle) ||
                ShortStoryOrSpecialNoiseRegex.IsMatch(fullTitle) ||
                SplitEditionRegex.IsMatch(fullTitle))
            {
                failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume contains conflicting or adaptation markers.");
                return null;
            }

            // Check if title or subtitle has ANY volume marker
            var parsedCand = MediaTitleParser.ParseTitle(info.Title ?? string.Empty);
            var candVolume = parsedCand.Volume ?? MediaTitleParser.ParseVolumeMarker(info.Title);
            StructuredVolume? subtitleVolume = null;

            if (!string.IsNullOrWhiteSpace(info.Subtitle))
            {
                var parsedSub = MediaTitleParser.ParseTitle(info.Subtitle);
                subtitleVolume = parsedSub.Volume ?? MediaTitleParser.ParseVolumeMarker(info.Subtitle);
            }

            if (candVolume is not null || subtitleVolume is not null)
            {
                failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume contains explicit volume marker conflicting with inferred status.");
                return null;
            }

            // Normalized base title exactly equals trusted Japanese base title.
            var trustedJapaneseVariants = GetTrustedJapaneseBaseTitleVariants(context)
                .Select(NormalizeTitle)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.Ordinal);

            var candBase = NormalizeTitle(parsedCand.BaseTitle);
            var rawTitleNormalized = NormalizeTitle(info.Title ?? string.Empty);
            if (candBase != rawTitleNormalized || !trustedJapaneseVariants.Contains(candBase))
            {
                failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume title did not match trusted Japanese base title.");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(info.Subtitle))
            {
                var subNorm = NormalizeTitle(info.Subtitle);
                if (subNorm.Length > 0 && !trustedJapaneseVariants.Contains(subNorm))
                {
                    failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                        warning: "Re-fetched volume subtitle did not match trusted Japanese base title.");
                    return null;
                }
            }

            // Valid normalized ISBN matching search-matched ISBN
            var isbn = GetNormalizedIsbn(info);
            if (string.IsNullOrWhiteSpace(isbn) ||
                !string.Equals(isbn, searchMatched.NormalizedIsbn, StringComparison.Ordinal))
            {
                failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched volume ISBN did not match verified search ISBN.");
                return null;
            }

            return searchMatched with { Volume = freshVolume };
        }

        failureResult = GoogleBooksCoverMatchResult.CreateNoMatch(
            warning: "Unknown Google Books identity proof type.");
        return null;
    }

    private static string RemoveTargetVolumeMarker(string title, StructuredVolume? targetVolume) =>
        targetVolume is null ? title : MediaTitleParser.ParseTitle(title).BaseTitle;
}
