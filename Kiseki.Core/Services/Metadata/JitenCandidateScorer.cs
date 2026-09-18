using System.Globalization;
using Kiseki.Core.Models.Metadata;

namespace Kiseki.Core.Services.Metadata;

public sealed class JitenCandidateScorer : IJitenCandidateScorer
{
    // Centralized scoring weights and thresholds
    public const int MaxTitleScore = 40;
    public const int PartialTitleScore = 20;

    public const int ExactVolumeScore = 35;

    public const int CloseTtsuCharScore = 20;     // <= 15%
    public const int ModerateTtsuCharScore = 10;  // > 15% && <= 30%

    public const int HighConfidenceThreshold = 85;
    public const int ReviewConfidenceThreshold = 60;
    public const int HighConfidenceMinMargin = 10;

    private readonly IMediaTitleParser _titleParser;

    public JitenCandidateScorer(IMediaTitleParser? titleParser = null)
    {
        _titleParser = titleParser ?? new MediaTitleParser();
    }

    public JitenMatchResult Score(
        ParsedMediaTitle parsedTitle,
        IEnumerable<JitenMatchCandidate> candidates,
        int? authoritativeTtsuTotal = null,
        int filteredIncompatibleCount = 0)
    {
        ArgumentNullException.ThrowIfNull(parsedTitle);
        ArgumentNullException.ThrowIfNull(candidates);

        var candidateList = candidates.ToList();
        var scoredList = new List<ScoredCandidate>();

        foreach (var candidate in candidateList)
        {
            scoredList.Add(ScoreSingle(parsedTitle, candidate, authoritativeTtsuTotal));
        }

        // Deterministic ordering:
        // 1. score descending
        // 2. display title using ordinal comparison
        // 3. parent deck ID
        // 4. subdeck ID, with a consistent null ordering
        var rankedCandidates = scoredList
            .OrderByDescending(c => c.TotalScore)
            .ThenBy(c => c.Candidate.DisplayTitle, StringComparer.Ordinal)
            .ThenBy(c => c.Candidate.DeckId)
            .ThenBy(c => c.Candidate.SubdeckId ?? int.MinValue)
            .ToList();

        var best = rankedCandidates.FirstOrDefault();
        var resultEvidence = new List<string>();

        if (best is null || best.IsDisqualified || best.TotalScore < ReviewConfidenceThreshold)
        {
            if (best?.IsDisqualified == true)
            {
                resultEvidence.Add(best.DisqualificationReason ?? "Top candidate was disqualified.");
            }
            else if (best is not null)
            {
                resultEvidence.Add(string.Create(CultureInfo.InvariantCulture, $"Score {best.TotalScore} below review threshold {ReviewConfidenceThreshold}."));
            }
            else
            {
                resultEvidence.Add("No candidates were supplied.");
            }

            return new JitenMatchResult
            {
                ParsedTitle = parsedTitle,
                Confidence = MatchConfidence.None,
                AuthoritativeTtsuTotal = authoritativeTtsuTotal,
                RunnerUpMargin = 0,
                Candidates = rankedCandidates,
                Evidence = resultEvidence,
                FilteredIncompatibleCount = filteredIncompatibleCount
            };
        }

        // Calculate runner-up margin
        var runnerUp = rankedCandidates.Skip(1).FirstOrDefault(c => !c.IsDisqualified);
        var runnerUpMargin = runnerUp is not null
            ? best.TotalScore - runnerUp.TotalScore
            : best.TotalScore;

        resultEvidence.AddRange(best.Evidence);

        // Check High Confidence criteria:
        // 1. Score >= 85
        // 2. Not disqualified
        // 3. Margin >= 10
        // 4. Base title was not a partial match
        // 5. Volume marker is not special or fractional
        // 6. If tentative (attached ASCII or implicit vol 1), character count sanity passes
        var isSpecial = parsedTitle.IsSpecialVolume || (best.CandidateVolume?.IsSpecial ?? false);
        var isTentative = parsedTitle.VolumeInference is VolumeInferenceKind.AttachedAsciiHypothesis or VolumeInferenceKind.ImplicitFirstVolume;
        bool tentativeSanityPassed = !isTentative || (authoritativeTtsuTotal.HasValue && best.CharacterCountScore >= ModerateTtsuCharScore);

        if (best.TotalScore >= HighConfidenceThreshold &&
            runnerUpMargin >= HighConfidenceMinMargin &&
            !best.IsPartialTitleMatch &&
            !isSpecial &&
            tentativeSanityPassed)
        {
            return new JitenMatchResult
            {
                ParsedTitle = parsedTitle,
                Confidence = MatchConfidence.High,
                AuthoritativeTtsuTotal = authoritativeTtsuTotal,
                RunnerUpMargin = runnerUpMargin,
                Candidates = rankedCandidates,
                Evidence = resultEvidence,
                FilteredIncompatibleCount = filteredIncompatibleCount
            };
        }

        if (isTentative && !tentativeSanityPassed)
        {
            resultEvidence.Add("Tentative volume hypothesis requires character-count confirmation or manual review");
        }

        if (isSpecial)
        {
            resultEvidence.Add("Special volume requires review");
        }

        if (best.TotalScore >= HighConfidenceThreshold && runnerUpMargin < HighConfidenceMinMargin)
        {
            resultEvidence.Add(string.Create(CultureInfo.InvariantCulture, $"Close runner-up (margin {runnerUpMargin} < {HighConfidenceMinMargin}) requires review"));
        }

        if (best.IsPartialTitleMatch)
        {
            resultEvidence.Add("Partial title match requires review");
        }

        return new JitenMatchResult
        {
            ParsedTitle = parsedTitle,
            Confidence = MatchConfidence.Review,
            AuthoritativeTtsuTotal = authoritativeTtsuTotal,
            RunnerUpMargin = runnerUpMargin,
            Candidates = rankedCandidates,
            Evidence = resultEvidence,
            FilteredIncompatibleCount = filteredIncompatibleCount
        };
    }

    private ScoredCandidate ScoreSingle(
        ParsedMediaTitle parsedTitle,
        JitenMatchCandidate candidate,
        int? authoritativeTtsuTotal)
    {
        var evidence = new List<string>();

        // Hard rule: A parent deck with children can never be linked to a single work
        if (candidate.ChildrenDeckCount > 0 && !candidate.SubdeckId.HasValue)
        {
            return new ScoredCandidate
            {
                Candidate = candidate,
                TotalScore = 0,
                TitleScore = 0,
                VolumeScore = 0,
                CharacterCountScore = 0,
                IsDisqualified = true,
                DisqualificationReason = "Parent deck has subdecks and cannot be linked directly to a work.",
                MatchedTitle = MatchedTitleVariant.None,
                IsPartialTitleMatch = false,
                CandidateVolume = null,
                Evidence = ["Parent deck has subdecks and cannot be linked directly to a work."]
            };
        }

        // 0. Series Qualifier Compatibility
        var requestQualifier = parsedTitle.SeriesQualifier;
        var candidateQualifier = MediaTitleParser.GetCandidateSeriesQualifier(candidate);

        if (requestQualifier != SeriesQualifier.Mainline)
        {
            if (candidateQualifier != requestQualifier)
            {
                var reason = $"Qualifier conflict: expected {requestQualifier} but candidate is {candidateQualifier}.";
                evidence.Add(reason);
                return new ScoredCandidate
                {
                    Candidate = candidate,
                    TotalScore = 0,
                    TitleScore = 0,
                    VolumeScore = 0,
                    CharacterCountScore = 0,
                    IsDisqualified = true,
                    DisqualificationReason = reason,
                    MatchedTitle = MatchedTitleVariant.None,
                    IsPartialTitleMatch = false,
                    CandidateVolume = null,
                    Evidence = evidence
                };
            }
            evidence.Add($"Series qualifier '{requestQualifier}' matched");
        }
        else
        {
            if (candidateQualifier is SeriesQualifier.ShortStories or SeriesQualifier.Ex or SeriesQualifier.ArtBook)
            {
                var reason = $"Qualifier conflict: mainline request does not match {candidateQualifier} branch.";
                evidence.Add(reason);
                return new ScoredCandidate
                {
                    Candidate = candidate,
                    TotalScore = 0,
                    TitleScore = 0,
                    VolumeScore = 0,
                    CharacterCountScore = 0,
                    IsDisqualified = true,
                    DisqualificationReason = reason,
                    MatchedTitle = MatchedTitleVariant.None,
                    IsPartialTitleMatch = false,
                    CandidateVolume = null,
                    Evidence = evidence
                };
            }
        }

        // Parse each non-empty child variant once per candidate
        ParsedMediaTitle? parsedOriginal = !string.IsNullOrWhiteSpace(candidate.OriginalTitle)
            ? _titleParser.Parse(candidate.OriginalTitle)
            : null;

        ParsedMediaTitle? parsedEnglish = !string.IsNullOrWhiteSpace(candidate.EnglishTitle)
            ? _titleParser.Parse(candidate.EnglishTitle)
            : null;

        ParsedMediaTitle? parsedRomaji = !string.IsNullOrWhiteSpace(candidate.RomajiTitle)
            ? _titleParser.Parse(candidate.RomajiTitle)
            : null;

        // Inspect candidate volumes and internal conflicts using centralized parser logic
        var extraction = MediaTitleParser.ExtractCandidateVolumes(candidate, _titleParser);
        var variantVolumes = extraction.VariantVolumes;
        var hasInternalConflict = extraction.HasInternalConflict;
        var internalConflictReason = extraction.InternalConflictReason;

        // 1. Base Title Matching (0 - 40)
        // For subdecks, primarily score against trusted parent deck title variants.
        // If parent titles are missing, fallback to child full-title variants.
        // Standalone decks score against their own title variants.
        int titleScore;
        MatchedTitleVariant matchedVariant;
        bool isPartialTitle;

        var effectiveBaseTitle = parsedTitle.VolumeInference == VolumeInferenceKind.AttachedAsciiHypothesis &&
                                 !string.IsNullOrWhiteSpace(parsedTitle.SearchPlan?.CanonicalBaseTitle)
            ? parsedTitle.SearchPlan.CanonicalBaseTitle
            : parsedTitle.BaseTitle;

        if (candidate.IsSubdeck)
        {
            var hasParentTitles = !string.IsNullOrWhiteSpace(candidate.ParentOriginalTitle) ||
                                  !string.IsNullOrWhiteSpace(candidate.ParentEnglishTitle) ||
                                  !string.IsNullOrWhiteSpace(candidate.ParentRomajiTitle);

            if (hasParentTitles)
            {
                ParsedMediaTitle? parsedParentOriginal = !string.IsNullOrWhiteSpace(candidate.ParentOriginalTitle)
                    ? _titleParser.Parse(candidate.ParentOriginalTitle)
                    : null;

                ParsedMediaTitle? parsedParentEnglish = !string.IsNullOrWhiteSpace(candidate.ParentEnglishTitle)
                    ? _titleParser.Parse(candidate.ParentEnglishTitle)
                    : null;

                ParsedMediaTitle? parsedParentRomaji = !string.IsNullOrWhiteSpace(candidate.ParentRomajiTitle)
                    ? _titleParser.Parse(candidate.ParentRomajiTitle)
                    : null;

                (titleScore, matchedVariant, isPartialTitle) = ScoreBaseTitleWithParsed(
                    effectiveBaseTitle,
                    parsedParentOriginal,
                    parsedParentEnglish,
                    parsedParentRomaji,
                    evidence);
            }
            else
            {
                (titleScore, matchedVariant, isPartialTitle) = ScoreBaseTitleWithParsed(
                    effectiveBaseTitle,
                    parsedOriginal,
                    parsedEnglish,
                    parsedRomaji,
                    evidence);
            }
        }
        else
        {
            (titleScore, matchedVariant, isPartialTitle) = ScoreBaseTitleWithParsed(
                effectiveBaseTitle,
                parsedOriginal,
                parsedEnglish,
                parsedRomaji,
                evidence);
        }

        // 2. Volume Identity Matching (0 - 35)
        int volumeScore = 0;
        bool isVolumeDisqualified = false;
        string? volumeDisqualificationReason = null;
        StructuredVolume? candidateVolume = null;

        if (hasInternalConflict)
        {
            isVolumeDisqualified = true;
            volumeDisqualificationReason = internalConflictReason;
            evidence.Add(internalConflictReason!);
        }
        else
        {
            // Prefer the matched variant's marker when available
            var matchedVolume = variantVolumes.FirstOrDefault(v => v.Variant == matchedVariant).Volume;
            candidateVolume = matchedVolume ?? extraction.PrimaryVolume ?? variantVolumes.FirstOrDefault().Volume;

            var effectiveWorkVolume = parsedTitle.Volume ??
                (parsedTitle.VolumeInference == VolumeInferenceKind.AttachedAsciiHypothesis ? parsedTitle.SearchPlan?.Volume : null);

            (volumeScore, isVolumeDisqualified, volumeDisqualificationReason) = ScoreVolume(
                effectiveWorkVolume,
                candidateVolume,
                candidate.IsStandalone,
                parsedTitle.VolumeInference,
                candidate.IsSubdeck,
                evidence);
        }

        // 3. Character Count Sanity (0 - 20)
        var charScore = ScoreCharacterCount(authoritativeTtsuTotal, candidate.CharacterCount, evidence);

        var isDisqualified = isVolumeDisqualified;
        var totalScore = isDisqualified ? 0 : titleScore + volumeScore + charScore;

        return new ScoredCandidate
        {
            Candidate = candidate,
            TotalScore = totalScore,
            TitleScore = titleScore,
            VolumeScore = volumeScore,
            CharacterCountScore = charScore,
            IsDisqualified = isDisqualified,
            DisqualificationReason = volumeDisqualificationReason,
            MatchedTitle = matchedVariant,
            IsPartialTitleMatch = isPartialTitle,
            CandidateVolume = candidateVolume,
            Evidence = evidence
        };
    }

    private static (int Score, MatchedTitleVariant Variant, bool IsPartial) ScoreBaseTitleWithParsed(
        string workBaseTitle,
        ParsedMediaTitle? original,
        ParsedMediaTitle? english,
        ParsedMediaTitle? romaji,
        List<string> evidence)
    {
        if (string.IsNullOrWhiteSpace(workBaseTitle))
        {
            evidence.Add("Work base title is empty");
            return (0, MatchedTitleVariant.None, false);
        }

        var variants = new[]
        {
            (original, MatchedTitleVariant.Original, "original"),
            (english, MatchedTitleVariant.English, "English"),
            (romaji, MatchedTitleVariant.Romaji, "romaji")
        };

        // First check exact matches
        foreach (var (parsed, variant, name) in variants)
        {
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.BaseTitle))
            {
                continue;
            }

            if (string.Equals(workBaseTitle, parsed.BaseTitle, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add($"Exact {name} base title matched");
                return (MaxTitleScore, variant, false);
            }
        }

        // Next check conservative partial matches:
        // One contains the other and covers at least 70% of length, with minimum 4 characters
        foreach (var (parsed, variant, name) in variants)
        {
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.BaseTitle))
            {
                continue;
            }

            if (IsConservativePartialMatch(workBaseTitle, parsed.BaseTitle))
            {
                evidence.Add($"Partial {name} base title match");
                return (PartialTitleScore, variant, true);
            }
        }

        evidence.Add("No base title match");
        return (0, MatchedTitleVariant.None, false);
    }

    private static bool IsConservativePartialMatch(string titleA, string titleB)
    {
        if (string.IsNullOrWhiteSpace(titleA) || string.IsNullOrWhiteSpace(titleB))
        {
            return false;
        }

        var minLength = Math.Min(titleA.Length, titleB.Length);
        var maxLength = Math.Max(titleA.Length, titleB.Length);

        if (minLength < 4 || (double)minLength / maxLength < 0.5)
        {
            return false;
        }

        return titleA.Contains(titleB, StringComparison.OrdinalIgnoreCase) ||
               titleB.Contains(titleA, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Score, bool IsDisqualified, string? Reason) ScoreVolume(
        StructuredVolume? workVolume,
        StructuredVolume? candidateVolume,
        bool isCandidateStandalone,
        VolumeInferenceKind volumeInference,
        bool isCandidateSubdeck,
        List<string> evidence)
    {
        // Case 1: Unnumbered title with implicit Volume 1
        if (workVolume is null && volumeInference == VolumeInferenceKind.ImplicitFirstVolume &&
            candidateVolume?.Kind == VolumeKind.Standard && candidateVolume?.Number == 1 &&
            isCandidateSubdeck)
        {
            evidence.Add("ImplicitFirstVolume: Unnumbered title matched parent deck with unique Volume 1 child");
            return (ExactVolumeScore, false, null);
        }

        // Case 2: Neither side has volume
        if (workVolume is null && candidateVolume is null)
        {
            // Verified standalone candidate with no child decks scores 35
            if (isCandidateStandalone)
            {
                evidence.Add("Standalone deck with no volume markers");
                return (ExactVolumeScore, false, null);
            }

            evidence.Add("No volume markers found on either title");
            return (0, false, null);
        }

        // Case 3: Both sides have volume
        if (workVolume is not null && candidateVolume is not null)
        {
            if (workVolume.Matches(candidateVolume))
            {
                if (volumeInference == VolumeInferenceKind.AttachedAsciiHypothesis)
                {
                    evidence.Add($"Provider-confirmed attached ASCII volume {workVolume.Number} hypothesis matched");
                }
                else
                {
                    evidence.Add($"{workVolume.FormatForEvidence()} matched");
                }
                return (ExactVolumeScore, false, null);
            }

            var conflictReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Explicit volume conflict: {workVolume.RawMarker} vs {candidateVolume.RawMarker}");
            evidence.Add(conflictReason);
            return (0, true, conflictReason);
        }

        // Case 4: One side has volume and the other does not
        evidence.Add("Volume marker missing on one side");
        return (0, false, null);
    }

    private static int ScoreCharacterCount(
        int? authoritativeTtsuTotal,
        int jitenCharacterCount,
        List<string> evidence)
    {
        if (!authoritativeTtsuTotal.HasValue || authoritativeTtsuTotal.Value <= 0 || jitenCharacterCount <= 0)
        {
            evidence.Add("No authoritative TTSU character count available");
            return 0;
        }

        var ttsu = (double)authoritativeTtsuTotal.Value;
        var jiten = (double)jitenCharacterCount;
        var diff = Math.Abs(ttsu - jiten) / Math.Max(ttsu, jiten);

        if (diff <= 0.15)
        {
            evidence.Add(string.Create(CultureInfo.InvariantCulture, $"TTSU total differs by {diff:P1}"));
            return CloseTtsuCharScore;
        }

        if (diff <= 0.30)
        {
            evidence.Add(string.Create(CultureInfo.InvariantCulture, $"TTSU total differs by {diff:P1}"));
            return ModerateTtsuCharScore;
        }

        evidence.Add(string.Create(CultureInfo.InvariantCulture, $"TTSU total differs by {diff:P1} (exceeds 30%)"));
        return 0;
    }
}
