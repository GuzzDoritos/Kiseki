using System.Text.Json;
using System.Text.Json.Serialization;
using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Tests;

public sealed class JitenMatchCalibrationTests
{
    private static readonly string[] RequiredFamilies =
    [
        "1_standalone",
        "2_numbered_series",
        "3_multilingual_consistency",
        "4_conflicting_variants",
        "5_close_margin",
        "6_partial_match",
        "7_fractional_volumes",
        "8_special_markers",
        "9_position_markers",
        "10_missing_totals",
        "11_character_boundaries",
        "12_misleading_numbers",
        "13_parent_disqualification",
        "14_child_expansion",
        "15_deduplication",
        "16_cover_neutrality",
        "17_parent_cover_fallback",
        "18_tied_candidates",
        "19_unrelated_candidates",
        "20_multibook_batch"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static CalibrationCorpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jiten-match-calibration.json");
        if (!File.Exists(path))
        {
            // Fallback for direct solution execution if output copy differs
            path = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "jiten-match-calibration.json");
        }
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CalibrationCorpus>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize calibration corpus.");
    }

    public static TheoryData<string> CalibrationCaseNames()
    {
        var corpus = LoadCorpus();
        var data = new TheoryData<string>();
        foreach (var c in corpus.Cases)
        {
            data.Add(c.CaseName);
        }
        return data;
    }

    [Fact]
    public void CalibrationCorpus_ContainsAllRequiredFamilies()
    {
        var corpus = LoadCorpus();
        Assert.True(corpus.Cases.Count >= 20, $"Expected at least 20 cases, found {corpus.Cases.Count}");

        var families = corpus.Cases.Select(c => c.Family).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(RequiredFamilies, family => !families.Contains(family));
        Assert.NotEmpty(corpus.MixedBatch.CaseNames);
        Assert.All(corpus.MixedBatch.CaseNames, caseName => Assert.Contains(corpus.Cases, testCase => testCase.CaseName == caseName));
    }

    [Fact]
    public async Task CalibrationCorpus_SafetyGate_HasZeroFalseHighMatches()
    {
        var corpus = LoadCorpus();
        var parser = new MediaTitleParser();
        var scorer = new JitenCandidateScorer(parser);
        var falseHighCases = new List<string>();

        foreach (var testCase in corpus.Cases)
        {
            var run = await RunCalibrationCaseAsync(testCase, parser, scorer);
            var outcome = run.Outcome;

            var isHigh = outcome.Result?.Confidence == MatchConfidence.High;
            if (isHigh)
            {
                if (!testCase.AutoSelectAllowed)
                {
                    falseHighCases.Add($"{testCase.CaseName}: Expected AutoSelectAllowed=false, but got High confidence.");
                }

                var topCandidate = outcome.Result?.Candidates.FirstOrDefault(c => !c.IsDisqualified);
                if (topCandidate is null)
                {
                    falseHighCases.Add($"{testCase.CaseName}: High confidence result had no valid non-disqualified winner.");
                }
                else
                {
                    if (testCase.ExpectedDeckId.HasValue && topCandidate.Candidate.DeckId != testCase.ExpectedDeckId.Value)
                    {
                        falseHighCases.Add($"{testCase.CaseName}: Wrong winner DeckId (expected {testCase.ExpectedDeckId}, got {topCandidate.Candidate.DeckId}).");
                    }

                    if (testCase.ExpectedSubdeckId != topCandidate.Candidate.SubdeckId)
                    {
                        falseHighCases.Add($"{testCase.CaseName}: Wrong winner SubdeckId (expected {testCase.ExpectedSubdeckId}, got {topCandidate.Candidate.SubdeckId}).");
                    }

                    if (outcome.Result?.RunnerUpMargin < JitenCandidateScorer.HighConfidenceMinMargin)
                    {
                        falseHighCases.Add($"{testCase.CaseName}: High confidence margin {outcome.Result.RunnerUpMargin} < {JitenCandidateScorer.HighConfidenceMinMargin}.");
                    }
                }
            }
        }

        Assert.True(
            falseHighCases.Count == 0,
            $"Safety Gate FAILED with {falseHighCases.Count} false High matches:\n" + string.Join("\n", falseHighCases));
    }

    [Theory]
    [MemberData(nameof(CalibrationCaseNames))]
    public async Task CalibrationCase_ProducesExpectedOutcome(string caseName)
    {
        var corpus = LoadCorpus();
        var testCase = corpus.Cases.Single(c => c.CaseName == caseName);
        var parser = new MediaTitleParser();
        var scorer = new JitenCandidateScorer(parser);

        var run = await RunCalibrationCaseAsync(testCase, parser, scorer);
        var outcome = run.Outcome;

        Assert.Equal(testCase.ExpectedStatus, outcome.Status);

        if (outcome.Result is not null)
        {
            Assert.Equal(testCase.ExpectedConfidence, outcome.Result.Confidence);

            var top = outcome.Result.Candidates.FirstOrDefault(candidate => !candidate.IsDisqualified);
            if (testCase.ExpectedDeckId.HasValue)
            {
                Assert.NotNull(top);
                Assert.Equal(testCase.ExpectedDeckId, top.Candidate.DeckId);
                Assert.Equal(testCase.ExpectedSubdeckId, top.Candidate.SubdeckId);
            }

            if (testCase.ExpectedCharacterCountScore.HasValue)
            {
                Assert.NotNull(top);
                Assert.Equal(testCase.ExpectedCharacterCountScore, top.CharacterCountScore);
            }

            if (testCase.ExpectedRunnerUpMargin.HasValue)
            {
                Assert.Equal(testCase.ExpectedRunnerUpMargin, outcome.Result.RunnerUpMargin);
            }

            if (testCase.ExpectedCoverEvidence.HasValue)
            {
                Assert.NotNull(top);
                Assert.Equal(testCase.ExpectedCoverEvidence, top.Candidate.CoverEvidence);
            }

            if (testCase.ExpectedConfidence == MatchConfidence.High)
            {
                Assert.True(testCase.AutoSelectAllowed, $"{caseName} was High but marked AutoSelectAllowed=false");
                Assert.NotNull(top);
                Assert.True(outcome.Result.RunnerUpMargin >= JitenCandidateScorer.HighConfidenceMinMargin);
            }
            else
            {
                Assert.False(testCase.AutoSelectAllowed, $"{caseName} was not High but marked AutoSelectAllowed=true");
            }

            foreach (var expectedEv in testCase.ExpectedEvidence)
            {
                Assert.Contains(outcome.Result.Evidence, ev => ev.Contains(expectedEv, StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            Assert.Equal(MatchConfidence.None, testCase.ExpectedConfidence);
            Assert.Empty(testCase.ExpectedEvidence);
        }

        var expectedDetailIds = testCase.DeckDetails
            .Select(detail => detail.MainDeck?.DeckId ?? detail.ParentDeck?.DeckId ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToList();
        Assert.Equal([parser.Parse(testCase.RawTitle).BaseTitle], run.Api.SearchCalls);
        Assert.Equal(expectedDetailIds, run.Api.DetailCalls.Order().ToList());
    }

    [Fact]
    public void Scorer_CoverPresenceAndParentFallback_HaveZeroEffectOnScore()
    {
        var parser = new MediaTitleParser();
        var scorer = new JitenCandidateScorer(parser);
        var parsedTitle = parser.Parse("Test Novel 1");

        var candidateWithCover = new JitenMatchCandidate
        {
            DeckId = 1,
            SubdeckId = 2,
            OriginalTitle = "Test Novel 1",
            CharacterCount = 100_000,
            CoverUrl = "https://cdn.jiten.moe/specific.jpg",
            CoverEvidence = JitenCoverEvidence.Specific
        };

        var candidateWithoutCover = new JitenMatchCandidate
        {
            DeckId = 1,
            SubdeckId = 2,
            OriginalTitle = "Test Novel 1",
            CharacterCount = 100_000,
            CoverUrl = null,
            CoverEvidence = JitenCoverEvidence.None
        };

        var candidateParentFallback = new JitenMatchCandidate
        {
            DeckId = 1,
            SubdeckId = 2,
            OriginalTitle = "Test Novel 1",
            CharacterCount = 100_000,
            CoverUrl = "https://cdn.jiten.moe/parent.jpg",
            CoverEvidence = JitenCoverEvidence.ParentFallback
        };

        var result1 = scorer.Score(parsedTitle, [candidateWithCover], authoritativeTtsuTotal: 100_000);
        var result2 = scorer.Score(parsedTitle, [candidateWithoutCover], authoritativeTtsuTotal: 100_000);
        var result3 = scorer.Score(parsedTitle, [candidateParentFallback], authoritativeTtsuTotal: 100_000);

        Assert.Equal(result1.Candidates[0].TotalScore, result2.Candidates[0].TotalScore);
        Assert.Equal(result1.Candidates[0].TotalScore, result3.Candidates[0].TotalScore);
        Assert.Equal(result1.Confidence, result2.Confidence);
        Assert.Equal(result1.Confidence, result3.Confidence);
    }

    [Fact]
    public void Scorer_SpecialAndFractionalVolumes_NeverProduceHighConfidence()
    {
        var parser = new MediaTitleParser();
        var scorer = new JitenCandidateScorer(parser);

        var specialTitles = new[]
        {
            "Test Novel 4.5",
            "Test Novel 10.5",
            "Test Novel Ex 1",
            "Test Novel 短編集 1",
            "Test Novel 2年生編 1",
            "Test Novel Ep.1"
        };

        foreach (var title in specialTitles)
        {
            var parsed = parser.Parse(title);
            Assert.True(parsed.IsSpecialVolume, $"Expected {title} to be marked IsSpecialVolume");

            var candidate = new JitenMatchCandidate
            {
                DeckId = 1,
                SubdeckId = 2,
                OriginalTitle = title,
                CharacterCount = 100_000
            };

            var result = scorer.Score(parsed, [candidate], authoritativeTtsuTotal: 100_000);
            Assert.NotEqual(MatchConfidence.High, result.Confidence);
            Assert.Equal(MatchConfidence.Review, result.Confidence);
        }
    }

    [Fact]
    public void Scorer_MissingAuthoritativeTotal_NeverProducesHighConfidence()
    {
        var parser = new MediaTitleParser();
        var scorer = new JitenCandidateScorer(parser);
        var parsed = parser.Parse("Test Novel 1");

        var candidate = new JitenMatchCandidate
        {
            DeckId = 1,
            SubdeckId = 2,
            OriginalTitle = "Test Novel 1",
            CharacterCount = 100_000
        };

        var result = scorer.Score(parsed, [candidate], authoritativeTtsuTotal: null);
        Assert.NotEqual(MatchConfidence.High, result.Confidence);
        Assert.Equal(MatchConfidence.Review, result.Confidence);
    }

    [Fact]
    public async Task MultiBookBatch_WithMixedOutcomes_ExecutesWithoutCrossContamination()
    {
        var corpus = LoadCorpus();
        var parser = new MediaTitleParser();
        var scorer = new JitenCandidateScorer(parser);
        var api = new FixtureJitenApiClient();
        var batchCases = corpus.MixedBatch.CaseNames
            .Select(caseName => corpus.Cases.Single(testCase => testCase.CaseName == caseName))
            .ToList();
        foreach (var testCase in batchCases)
        {
            ConfigureCase(api, parser, testCase);
        }

        var unavailableParsed = parser.Parse(corpus.MixedBatch.UnavailableRawTitle);
        api.AddSearchFailure(unavailableParsed.BaseTitle,
            new JitenHttpException("Server Error", System.Net.HttpStatusCode.InternalServerError, null));

        var matchService = CreateMatchService(api, parser, scorer);
        var requests = batchCases.Select(testCase => new JitenMatchRequest
        {
            CorrelationId = Guid.NewGuid(),
            RawTitle = testCase.RawTitle,
            AuthoritativeTtsuTotal = testCase.AuthoritativeTotal
        }).ToList();
        var unavailableRequest = new JitenMatchRequest
        {
            CorrelationId = Guid.NewGuid(),
            RawTitle = corpus.MixedBatch.UnavailableRawTitle,
            AuthoritativeTtsuTotal = 50_000
        };
        requests.Add(unavailableRequest);

        var outcomes = await matchService.MatchBatchAsync(requests, TimeSpan.FromSeconds(5));

        for (var index = 0; index < batchCases.Count; index++)
        {
            var expected = batchCases[index];
            var outcome = outcomes.Single(item => item.CorrelationId == requests[index].CorrelationId);
            Assert.Equal(expected.ExpectedStatus, outcome.Status);
            Assert.Equal(expected.ExpectedConfidence, outcome.Result?.Confidence ?? MatchConfidence.None);
        }

        Assert.Equal(JitenMatchStatus.Unavailable,
            outcomes.Single(item => item.CorrelationId == unavailableRequest.CorrelationId).Status);
        Assert.Equal(requests.Count, api.SearchCalls.Count);
        Assert.Equal(api.DetailCalls.Distinct().Count(), api.DetailCalls.Count);
    }

    private static async Task<CalibrationRun> RunCalibrationCaseAsync(
        CalibrationCase testCase,
        IMediaTitleParser parser,
        IJitenCandidateScorer scorer)
    {
        var api = new FixtureJitenApiClient();
        ConfigureCase(api, parser, testCase);
        var matchService = CreateMatchService(api, parser, scorer);

        var request = new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = testCase.RawTitle, AuthoritativeTtsuTotal = testCase.AuthoritativeTotal };
        var outcomes = await matchService.MatchBatchAsync([request], TimeSpan.FromSeconds(5));

        return new CalibrationRun(Assert.Single(outcomes), api);
    }

    private static void ConfigureCase(FixtureJitenApiClient api, IMediaTitleParser parser, CalibrationCase testCase)
    {
        var parsed = parser.Parse(testCase.RawTitle);
        api.AddSearch(parsed.BaseTitle, testCase.SearchResults);
        foreach (var detail in testCase.DeckDetails)
        {
            var id = detail.MainDeck?.DeckId ?? detail.ParentDeck?.DeckId ?? 0;
            if (id > 0)
            {
                api.AddDetail(id, detail);
            }
        }
    }

    private static JitenMatchService CreateMatchService(
        FixtureJitenApiClient api,
        IMediaTitleParser parser,
        IJitenCandidateScorer scorer) =>
        new(
            api,
            new JitenSelectionResolver(api),
            parser,
            scorer,
            new JitenMatchOptions
            {
                MaxConcurrency = 3,
                MaxRetries = 0,
                BaseRetryDelay = TimeSpan.Zero,
                MaxRetryDelay = TimeSpan.Zero
            },
            (_, _) => Task.CompletedTask);

    private sealed record CalibrationRun(JitenMatchOutcome Outcome, FixtureJitenApiClient Api);

    private sealed class FixtureJitenApiClient : IJitenApiClient
    {
        private readonly Dictionary<string, List<JitenDeckDTO>> _searches = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _searchFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, JitenDeckDetailDTO> _details = [];
        public List<string> SearchCalls { get; } = [];
        public List<int> DetailCalls { get; } = [];

        public void AddSearch(string query, List<JitenDeckDTO> results) => _searches[query] = results;
        public void AddSearchFailure(string query, Exception ex) => _searchFailures[query] = ex;
        public void AddDetail(int deckId, JitenDeckDetailDTO detail) => _details[deckId] = detail;

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls.Add(query);
            if (_searchFailures.TryGetValue(query, out var ex))
            {
                return Task.FromException<IReadOnlyList<JitenDeckDTO>>(ex);
            }
            var results = _searches.GetValueOrDefault(query) ?? [];
            return Task.FromResult<IReadOnlyList<JitenDeckDTO>>(results);
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetailCalls.Add(deckId);
            return Task.FromResult(_details.GetValueOrDefault(deckId));
        }

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<JitenFranchiseDTO?>(null);
        }
    }

    public sealed class CalibrationCorpus
    {
        public CalibrationBatch MixedBatch { get; set; } = new();

        [JsonPropertyName("cases")]
        public List<CalibrationCase> Cases { get; set; } = [];
    }

    public sealed class CalibrationBatch
    {
        public List<string> CaseNames { get; set; } = [];
        public string UnavailableRawTitle { get; set; } = string.Empty;
    }

    public sealed class CalibrationCase
    {
        public string CaseName { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string RawTitle { get; set; } = string.Empty;
        public int? AuthoritativeTotal { get; set; }
        public List<JitenDeckDTO> SearchResults { get; set; } = [];
        public List<JitenDeckDetailDTO> DeckDetails { get; set; } = [];
        public JitenMatchStatus ExpectedStatus { get; set; }
        public MatchConfidence ExpectedConfidence { get; set; }
        public int? ExpectedDeckId { get; set; }
        public int? ExpectedSubdeckId { get; set; }
        public bool AutoSelectAllowed { get; set; }
        public int? ExpectedCharacterCountScore { get; set; }
        public int? ExpectedRunnerUpMargin { get; set; }
        public JitenCoverEvidence? ExpectedCoverEvidence { get; set; }
        public List<string> ExpectedEvidence { get; set; } = [];
    }
}
