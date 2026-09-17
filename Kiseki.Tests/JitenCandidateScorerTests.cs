using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Tests;

public sealed class JitenCandidateScorerTests
{
    private readonly MediaTitleParser _parser = new();
    private readonly JitenCandidateScorer _scorer = new();

    [Fact]
    public void Score_MatchesAcrossOriginalEnglishAndRomajiVariants()
    {
        var candidateOriginal = new JitenMatchCandidate
        {
            DeckId = 1,
            OriginalTitle = "ソードアート・オンライン 1",
            CharacterCount = 100_000
        };
        var candidateEnglish = new JitenMatchCandidate
        {
            DeckId = 2,
            OriginalTitle = "SAO 1",
            EnglishTitle = "Sword Art Online Vol. 1",
            CharacterCount = 100_000
        };
        var candidateRomaji = new JitenMatchCandidate
        {
            DeckId = 3,
            OriginalTitle = "SAO 1",
            RomajiTitle = "Sword Art Online 1",
            CharacterCount = 100_000
        };

        var parsedJa = _parser.Parse("ソードアート・オンライン 1");
        var resultJa = _scorer.Score(parsedJa, [candidateOriginal], 100_000);
        Assert.Equal(MatchedTitleVariant.Original, resultJa.BestCandidate!.MatchedTitle);
        Assert.Equal(40, resultJa.BestCandidate.TitleScore);

        var parsedEn = _parser.Parse("Sword Art Online 1");
        var resultEn = _scorer.Score(parsedEn, [candidateEnglish], 100_000);
        Assert.Equal(MatchedTitleVariant.English, resultEn.BestCandidate!.MatchedTitle);
        Assert.Equal(40, resultEn.BestCandidate.TitleScore);

        var parsedRo = _parser.Parse("Sword Art Online 1");
        var resultRo = _scorer.Score(parsedRo, [candidateRomaji], 100_000);
        Assert.Equal(MatchedTitleVariant.Romaji, resultRo.BestCandidate!.MatchedTitle);
        Assert.Equal(40, resultRo.BestCandidate.TitleScore);
    }

    [Fact]
    public void Score_ExactVolumeScores35_ConflictingVolumeDisqualifies()
    {
        var parsed = _parser.Parse("Overlord Vol. 1");

        var matchingCandidate = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Overlord 第1巻",
            CharacterCount = 100_000
        };

        var conflictingCandidate = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 12,
            OriginalTitle = "Overlord 第2巻",
            CharacterCount = 100_000
        };

        var result = _scorer.Score(parsed, [matchingCandidate, conflictingCandidate], 100_000);

        var scoredMatching = result.Candidates.Single(c => c.Candidate.SubdeckId == 11);
        Assert.Equal(35, scoredMatching.VolumeScore);
        Assert.False(scoredMatching.IsDisqualified);
        Assert.Contains("Volume 1 matched", scoredMatching.Evidence);

        var scoredConflicting = result.Candidates.Single(c => c.Candidate.SubdeckId == 12);
        Assert.True(scoredConflicting.IsDisqualified);
        Assert.Equal(0, scoredConflicting.TotalScore);
        Assert.Contains("Explicit volume conflict", scoredConflicting.DisqualificationReason);
    }

    [Fact]
    public void Score_VerifiedStandaloneCandidateWithNoVolumeScores35()
    {
        var parsed = _parser.Parse("Standalone Masterpiece");

        var standalone = new JitenMatchCandidate
        {
            DeckId = 100,
            SubdeckId = null,
            ChildrenDeckCount = 0,
            OriginalTitle = "Standalone Masterpiece",
            CharacterCount = 100_000
        };

        var subdeckWithoutVolume = new JitenMatchCandidate
        {
            DeckId = 200,
            SubdeckId = 201,
            ChildrenDeckCount = 0,
            OriginalTitle = "Standalone Masterpiece",
            CharacterCount = 100_000
        };

        var result = _scorer.Score(parsed, [standalone, subdeckWithoutVolume], 100_000);

        var scoredStandalone = result.Candidates.Single(c => c.Candidate.DeckId == 100);
        Assert.Equal(35, scoredStandalone.VolumeScore);

        var scoredSubdeck = result.Candidates.Single(c => c.Candidate.DeckId == 200);
        Assert.Equal(0, scoredSubdeck.VolumeScore);
    }

    [Fact]
    public void Score_MissingVolumeMarkerOnOneSideScoresZeroPoints()
    {
        var parsedWithVol = _parser.Parse("Spice and Wolf Vol. 1");
        var candidateWithoutVol = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Spice and Wolf",
            CharacterCount = 100_000
        };

        var result1 = _scorer.Score(parsedWithVol, [candidateWithoutVol], 100_000);
        var scored1 = Assert.Single(result1.Candidates);
        Assert.Equal(0, scored1.VolumeScore);
        Assert.False(scored1.IsDisqualified);

        var parsedWithoutVol = _parser.Parse("Spice and Wolf");
        var candidateWithVol = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Spice and Wolf 1",
            CharacterCount = 100_000
        };

        var result2 = _scorer.Score(parsedWithoutVol, [candidateWithVol], 100_000);
        var scored2 = Assert.Single(result2.Candidates);
        Assert.Equal(0, scored2.VolumeScore);
        Assert.False(scored2.IsDisqualified);
    }

    [Theory]
    [InlineData(100_000, 100_000, 20)]  // 0% diff -> 20
    [InlineData(100_000, 85_000, 20)]   // 15% diff -> 20
    [InlineData(100_000, 84_000, 10)]   // 16% diff -> 10
    [InlineData(100_000, 70_000, 10)]   // 30% diff -> 10
    [InlineData(100_000, 69_000, 0)]    // 31% diff -> 0
    [InlineData(null, 100_000, 0)]      // missing total -> 0
    [InlineData(0, 100_000, 0)]         // non-positive -> 0
    public void Score_CharacterCountBoundaries(
        int? ttsuTotal,
        int jitenCount,
        int expectedScore)
    {
        var parsed = _parser.Parse("Title 1");
        var candidate = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Title 1",
            CharacterCount = jitenCount
        };

        var result = _scorer.Score(parsed, [candidate], ttsuTotal);
        var scored = Assert.Single(result.Candidates);

        Assert.Equal(expectedScore, scored.CharacterCountScore);
    }

    [Fact]
    public void Score_CoverDoesNotContributeToScore()
    {
        var parsed = _parser.Parse("Title 1");

        var withCover = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Title 1",
            CharacterCount = 100_000,
            CoverUrl = "https://cdn.jiten.moe/cover.jpg"
        };

        var withoutCover = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Title 1",
            CharacterCount = 100_000,
            CoverUrl = null
        };

        var scoreWith = _scorer.Score(parsed, [withCover], 100_000).Candidates[0].TotalScore;
        var scoreWithout = _scorer.Score(parsed, [withoutCover], 100_000).Candidates[0].TotalScore;

        Assert.Equal(scoreWith, scoreWithout);
    }

    [Fact]
    public void Score_ParentDeckWithChildrenIsDisqualified()
    {
        var parsed = _parser.Parse("Series Title");
        var parentSeries = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = null,
            ChildrenDeckCount = 3,
            OriginalTitle = "Series Title",
            CharacterCount = 500_000
        };

        var result = _scorer.Score(parsed, [parentSeries], 500_000);
        var scored = Assert.Single(result.Candidates);

        Assert.True(scored.IsDisqualified);
        Assert.Equal(0, scored.TotalScore);
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void Score_HighConfidence_RequiresScoreAtLeast85AndLeadAtLeast10()
    {
        var parsed = _parser.Parse("Spice and Wolf 1");

        var best = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Spice and Wolf 1",
            CharacterCount = 100_000
        };

        var runnerUpClose = new JitenMatchCandidate
        {
            DeckId = 20,
            SubdeckId = 21,
            OriginalTitle = "Spice and Wolf 1",
            CharacterCount = 75_000 // 25% diff -> 10 pts, total 40+35+10 = 85 (lead = 10)
        };

        // Lead of 10 -> High
        var resultHigh = _scorer.Score(parsed, [best, runnerUpClose], 100_000);
        Assert.Equal(95, resultHigh.BestCandidate!.TotalScore);
        Assert.Equal(85, resultHigh.Candidates[1].TotalScore);
        Assert.Equal(10, resultHigh.RunnerUpMargin);
        Assert.Equal(MatchConfidence.High, resultHigh.Confidence);

        // Lead of 5 (< 10) -> Review
        var runnerUpVeryClose = new JitenMatchCandidate
        {
            DeckId = 30,
            SubdeckId = 31,
            OriginalTitle = "Spice and Wolf 1",
            CharacterCount = 92_000 // 8% diff -> 20 pts (identical score 95)
        };
        var resultTie = _scorer.Score(parsed, [best, runnerUpVeryClose], 100_000);
        Assert.Equal(0, resultTie.RunnerUpMargin);
        Assert.Equal(MatchConfidence.Review, resultTie.Confidence);
    }

    [Fact]
    public void Score_SpecialAndFractionalVolumesCapAtReview()
    {
        // 4.5 fractional volume
        var parsedFractional = _parser.Parse("Re:Zero 4.5");
        var candidateFractional = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Re:Zero 4.5",
            CharacterCount = 100_000
        };

        var resultFractional = _scorer.Score(parsedFractional, [candidateFractional], 100_000);
        Assert.Equal(95, resultFractional.BestCandidate!.TotalScore);
        Assert.Equal(MatchConfidence.Review, resultFractional.Confidence);
        Assert.Contains("Special volume requires review", resultFractional.Evidence);

        // Ep.1 special volume
        var parsedSpecial = _parser.Parse("86 Ep.1");
        var candidateSpecial = new JitenMatchCandidate
        {
            DeckId = 20,
            SubdeckId = 21,
            OriginalTitle = "86 Ep.1",
            CharacterCount = 100_000
        };

        var resultSpecial = _scorer.Score(parsedSpecial, [candidateSpecial], 100_000);
        Assert.Equal(95, resultSpecial.BestCandidate!.TotalScore);
        Assert.Equal(MatchConfidence.Review, resultSpecial.Confidence);
    }

    [Fact]
    public void Score_DeterministicOrderingOnTies()
    {
        var parsed = _parser.Parse("Title 1");

        var c1 = new JitenMatchCandidate
        {
            DeckId = 20,
            SubdeckId = 1,
            OriginalTitle = "B Title",
            CharacterCount = 100_000
        };
        var c2 = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 2,
            OriginalTitle = "A Title",
            CharacterCount = 100_000
        };
        var c3 = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 1,
            OriginalTitle = "A Title",
            CharacterCount = 100_000
        };

        var result = _scorer.Score(parsed, [c1, c2, c3], 100_000);

        // Ordered by:
        // 1. TotalScore descending
        // 2. DisplayTitle ordinal ("A Title" before "B Title")
        // 3. DeckId ascending (10 before 20)
        // 4. SubdeckId ascending (1 before 2)
        Assert.Equal(c3.SubdeckId, result.Candidates[0].Candidate.SubdeckId);
        Assert.Equal(c2.SubdeckId, result.Candidates[1].Candidate.SubdeckId);
        Assert.Equal(c1.SubdeckId, result.Candidates[2].Candidate.SubdeckId);
    }

    [Fact]
    public void Score_PartialTitleMatchCapsAtReview()
    {
        var parsed = _parser.Parse("Sword Art Online Progressive 1");
        var candidate = new JitenMatchCandidate
        {
            DeckId = 10,
            SubdeckId = 11,
            OriginalTitle = "Sword Art Online 1", // partial containment match
            CharacterCount = 100_000
        };

        var result = _scorer.Score(parsed, [candidate], 100_000);

        Assert.Equal(MatchConfidence.Review, result.Confidence);
        Assert.True(result.BestCandidate!.IsPartialTitleMatch);
    }
}

