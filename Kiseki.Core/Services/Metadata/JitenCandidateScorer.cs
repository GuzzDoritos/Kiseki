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
        int? authoritativeTtsuTotal = null)
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
                Evidence = resultEvidence
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
        var isSpecial = parsedTitle.IsSpecialVolume || (best.CandidateVolume?.IsSpecial ?? false);

        if (best.TotalScore >= HighConfidenceThreshold &&
            runnerUpMargin >= HighConfidenceMinMargin &&
            !best.IsPartialTitleMatch &&
            !isSpecial)
        {
            return new JitenMatchResult
            {
                ParsedTitle = parsedTitle,
                Confidence = MatchConfidence.High,
                AuthoritativeTtsuTotal = authoritativeTtsuTotal,
                RunnerUpMargin = runnerUpMargin,
                Candidates = rankedCandidates,
                Evidence = resultEvidence
            };
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
            Evidence = resultEvidence
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

        // Parse each non-empty variant once per candidate
        ParsedMediaTitle? parsedOriginal = !string.IsNullOrWhiteSpace(candidate.OriginalTitle)
            ? _titleParser.Parse(candidate.OriginalTitle)
            : null;

        ParsedMediaTitle? parsedEnglish = !string.IsNullOrWhiteSpace(candidate.EnglishTitle)
            ? _titleParser.Parse(candidate.EnglishTitle)
            : null;

        ParsedMediaTitle? parsedRomaji = !string.IsNullOrWhiteSpace(candidate.RomajiTitle)
            ? _titleParser.Parse(candidate.RomajiTitle)
            : null;

        // Collect all explicit volumes across non-empty variants
        var variantVolumes = new List<(MatchedTitleVariant Variant, StructuredVolume Volume)>();
        if (parsedOriginal?.Volume is not null)
        {
            variantVolumes.Add((MatchedTitleVariant.Original, parsedOriginal.Volume));
        }
        if (parsedEnglish?.Volume is not null)
        {
            variantVolumes.Add((MatchedTitleVariant.English, parsedEnglish.Volume));
        }
        if (parsedRomaji?.Volume is not null)
        {
            variantVolumes.Add((MatchedTitleVariant.Romaji, parsedRomaji.Volume));
        }

        // Check for conflicting volumes across candidate title variants
        bool hasInternalConflict = false;
        string? internalConflictReason = null;
        for (var i = 0; i < variantVolumes.Count; i++)
        {
            for (var j = i + 1; j < variantVolumes.Count; j++)
            {
                if (variantVolumes[i].Volume.ConflictsWith(variantVolumes[j].Volume))
                {
                    hasInternalConflict = true;
                    internalConflictReason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Conflicting volume markers across candidate title variants: {variantVolumes[i].Variant} ({variantVolumes[i].Volume.RawMarker}) vs {variantVolumes[j].Variant} ({variantVolumes[j].Volume.RawMarker})");
                    break;
                }
            }
            if (hasInternalConflict)
            {
                break;
            }
        }

        // 1. Base Title Matching (0 - 40)
        var (titleScore, matchedVariant, isPartialTitle) = ScoreBaseTitleWithParsed(
            parsedTitle.BaseTitle,
            parsedOriginal,
            parsedEnglish,
            parsedRomaji,
            evidence);

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
            if (matchedVariant == MatchedTitleVariant.Original && parsedOriginal?.Volume is not null)
            {
                candidateVolume = parsedOriginal.Volume;
            }
            else if (matchedVariant == MatchedTitleVariant.English && parsedEnglish?.Volume is not null)
            {
                candidateVolume = parsedEnglish.Volume;
            }
            else if (matchedVariant == MatchedTitleVariant.Romaji && parsedRomaji?.Volume is not null)
            {
                candidateVolume = parsedRomaji.Volume;
            }
            else
            {
                candidateVolume = variantVolumes.FirstOrDefault().Volume;
            }

            (volumeScore, isVolumeDisqualified, volumeDisqualificationReason) = ScoreVolume(
                parsedTitle.Volume,
                candidateVolume,
                candidate.IsStandalone,
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
        List<string> evidence)
    {
        // Case 1: Neither side has volume
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

        // Case 2: Both sides have volume
        if (workVolume is not null && candidateVolume is not null)
        {
            if (workVolume.Matches(candidateVolume))
            {
                evidence.Add($"{workVolume.FormatForEvidence()} matched");
                return (ExactVolumeScore, false, null);
            }

            var conflictReason = string.Create(
                CultureInfo.InvariantCulture,
                $"Explicit volume conflict: {workVolume.RawMarker} vs {candidateVolume.RawMarker}");
            evidence.Add(conflictReason);
            return (0, true, conflictReason);
        }

        // Case 3: One side has volume and the other does not
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
