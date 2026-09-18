using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kiseki.Core.Models.Metadata;

namespace Kiseki.Core.Services.Metadata;

public sealed class MediaTitleParser : IMediaTitleParser
{
    private static readonly HashSet<string> RecognizedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // Light novel imprints & publishers
        "電撃文庫", "角川スニーカー文庫", "スニーカー文庫", "MF文庫J", "MF文庫",
        "ファミ通文庫", "ガガガ文庫", "オーバーラップ文庫", "オーバーラップノベルス",
        "HJ文庫", "HJノベルス", "富士見ファンタジア文庫", "ファンタジア文庫",
        "講談社ラノベ文庫", "ヒーロー文庫", "ダッシュエックス文庫", "GA文庫",
        "GAノベル", "TO文庫", "TOブックス", "カドカワBOOKS", "モンスター文庫",
        "アース・スターノベル", "アース・スター", "集英社", "小学館", "講談社",
        "KADOKAWA", "角川",
        // Release formats & common tags
        "Light Novel", "LightNovel", "Novel", "LN", "Book", "epub", "raw", "完",
        "小説", "文庫", "単行本"
    };

    private static readonly string[] FileSuffixes = [".epub", ".html", ".htm", ".txt"];

    private static readonly Dictionary<char, int> RomanMap = new()
    {
        ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000
    };

    public ParsedMediaTitle Parse(string rawTitle) => ParseTitle(rawTitle);

    /// <summary>
    /// Parses a value that is already known to be a volume-label field, such as
    /// a Jiten subdeck name or Google Books subtitle. Unlike <see cref="ParseTitle"/>,
    /// this may accept a marker-only value such as "Volume 1" or "第1巻".
    /// </summary>
    public static StructuredVolume? ParseVolumeMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string sentinel = "KisekiVolumeMarker";
        var parsed = ParseTitle($"{sentinel} {value.Trim()}");
        return string.Equals(parsed.BaseTitle, sentinel, StringComparison.Ordinal)
            ? parsed.Volume
            : null;
    }

    /// <summary>
    /// Extracts a volume from a child deck/subdeck title or marker using centralized parsing.
    /// </summary>
    public static StructuredVolume? ExtractChildVolume(string? title, bool isSubdeck, IMediaTitleParser? parser = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var parsed = parser?.Parse(title) ?? ParseTitle(title);
        if (parsed.Volume is not null)
        {
            return parsed.Volume;
        }

        if (isSubdeck)
        {
            return ParseVolumeMarker(title);
        }

        return null;
    }

    /// <summary>
    /// Inspects all title variants of a candidate for volume markers and detects any internal conflicts.
    /// </summary>
    public static CandidateVolumeExtraction ExtractCandidateVolumes(
        JitenMatchCandidate candidate,
        IMediaTitleParser? parser = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var variantVolumes = new List<(MatchedTitleVariant Variant, StructuredVolume Volume)>();
        var volOriginal = ExtractChildVolume(candidate.OriginalTitle, candidate.IsSubdeck, parser);
        if (volOriginal is not null)
        {
            variantVolumes.Add((MatchedTitleVariant.Original, volOriginal));
        }

        var volEnglish = ExtractChildVolume(candidate.EnglishTitle, candidate.IsSubdeck, parser);
        if (volEnglish is not null)
        {
            variantVolumes.Add((MatchedTitleVariant.English, volEnglish));
        }

        var volRomaji = ExtractChildVolume(candidate.RomajiTitle, candidate.IsSubdeck, parser);
        if (volRomaji is not null)
        {
            variantVolumes.Add((MatchedTitleVariant.Romaji, volRomaji));
        }

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

        var primary = !hasInternalConflict && variantVolumes.Count > 0
            ? variantVolumes[0].Volume
            : null;

        return new CandidateVolumeExtraction(
            variantVolumes,
            hasInternalConflict,
            internalConflictReason,
            primary);
    }

    public static ParsedMediaTitle ParseTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return new ParsedMediaTitle
            {
                OriginalTitle = rawTitle ?? string.Empty,
                ComparisonTitle = string.Empty,
                BaseTitle = string.Empty,
                Volume = null,
                ParsingNotes = ["Empty or whitespace-only title"]
            };
        }

        var notes = new List<string>();
        var transformations = new List<TitleTransformation>();
        var extractedAliases = new List<string>();

        // 0. Decode HTML entities for input cleanup (e.g. &amp;, &lt;, &gt;)
        var htmlDecoded = System.Net.WebUtility.HtmlDecode(rawTitle);

        // Pre-NFKC: check for attached full-width terminal digits before normalization converts them to ASCII
        var attachedFullWidthDigits = DetectAttachedFullWidthTerminalNumber(htmlDecoded);

        // 1. NFKC normalization and whitespace collapse
        var normalized = htmlDecoded.Normalize(NormalizationForm.FormKC);
        var collapsed = CollapseWhitespace(normalized);
        var comparisonTitle = collapsed;

        var current = collapsed;

        // 2. Strip recognized file suffixes (.epub, .html, .txt)
        foreach (var suffix in FileSuffixes)
        {
            if (current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var previous = current;
                current = current[..^suffix.Length].Trim();
                notes.Add($"Removed file suffix '{suffix}'");
                transformations.Add(new TitleTransformation("FileSuffix", $"Removed file suffix '{suffix}'", previous, current));
                break;
            }
        }

        // 3. Strip recognized leading bracketed release/publisher tags
        current = StripLeadingTags(current, notes, transformations);

        // 3.5. Strip recognized trailing metadata (series quotes, imprint parentheses, edition extras, angle bracket retailer annotations)
        current = StripTrailingMetadata(current, notes, transformations, extractedAliases);

        // 4. Extract terminal volume marker
        var (baseTitle, volume) = ExtractVolumeMarker(current, notes, attachedFullWidthDigits);

        // 5. Check for tentative attached ASCII terminal number hypothesis when no explicit volume was found
        StructuredVolume? tentativeVolume = null;
        var volumeInference = volume is not null ? VolumeInferenceKind.Explicit : VolumeInferenceKind.None;
        string canonicalBase = baseTitle;

        if (volume is null)
        {
            var (asciiNum, asciiBase) = DetectAttachedAsciiTerminalNumber(current);
            if (asciiNum is not null && asciiBase is not null)
            {
                if (int.TryParse(asciiNum, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    tentativeVolume = StructuredVolume.Standard(parsedInt, asciiNum);
                    volumeInference = VolumeInferenceKind.AttachedAsciiHypothesis;
                    canonicalBase = asciiBase;
                    notes.Add($"Identified attached ASCII terminal volume hypothesis '{asciiNum}'");
                    transformations.Add(new TitleTransformation(
                        "AttachedAsciiVolume",
                        $"Identified attached ASCII terminal volume hypothesis '{asciiNum}'",
                        current,
                        $"{asciiBase} (Volume {asciiNum})"));
                }
            }
        }

        // 6. Extract Series Qualifier and generate bounded search aliases
        var seriesQualifier = ExtractSeriesQualifier(rawTitle);
        var searchAliases = GenerateSearchAliases(baseTitle, canonicalBase, seriesQualifier, volumeInference, extractedAliases);

        var searchPlan = new TitleSearchPlan
        {
            OriginalTitle = rawTitle,
            ComparisonTitle = comparisonTitle,
            CanonicalBaseTitle = canonicalBase,
            Volume = volume ?? tentativeVolume,
            SeriesQualifier = seriesQualifier,
            VolumeInference = volumeInference,
            Transformations = transformations,
            SearchAliases = searchAliases,
            Notes = notes
        };

        return new ParsedMediaTitle
        {
            OriginalTitle = rawTitle,
            ComparisonTitle = comparisonTitle,
            BaseTitle = baseTitle,
            Volume = volume,
            ParsingNotes = notes,
            SearchPlan = searchPlan
        };
    }

    private static string? DetectAttachedFullWidthTerminalNumber(string rawTitle)
    {
        var text = rawTitle.Trim();
        foreach (var suffix in FileSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].Trim();
                break;
            }
        }

        // Must end with full-width digits (and optional full-width/half-width decimal point and full-width digits)
        // preceded by a non-whitespace, non-digit character (so it is attached to title text).
        var match = Regex.Match(text, @"(?<=[^\s\d\uFF10-\uFF19])([\uFF10-\uFF19]+(?:[\uFF0E.][\uFF10-\uFF19]+)?)$");
        if (!match.Success)
        {
            return null;
        }

        // Fail closed on oversized numbers
        var normalizedDigits = match.Groups[1].Value.Normalize(NormalizationForm.FormKC);
        if (normalizedDigits.Contains('.'))
        {
            if (!decimal.TryParse(normalizedDigits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            {
                return null;
            }
        }
        else
        {
            if (!int.TryParse(normalizedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return null;
            }
        }

        return match.Groups[1].Value;
    }

    private static readonly Regex RetailerSeriesRegex = new(
        @"[\s_]*『([^』]+)』シリーズ(?:\s*[\(（][^）\)]+[\)）])?$",
        RegexOptions.Compiled);

    private static readonly Regex RetailerAngleBracketRegex = new(
        @"[\s_]*(?:<([^>]+)>|〈([^〉]+)〉|《([^》]+)》)(?:\s*[\(（][^）\)]+[\)）])?$",
        RegexOptions.Compiled);

    private static readonly Regex EditionExtraRegex = new(
        @"[\s_]*【([^】]+)】$",
        RegexOptions.Compiled);

    private static readonly Regex ParenthesizedImprintRegex = new(
        @"[\s_]*[\(（]([^\)）]+)[\)）]$",
        RegexOptions.Compiled);

    private static string StripLeadingTags(string text, List<string> notes, List<TitleTransformation>? transformations = null)
    {
        var tagRegex = new Regex(@"^(?:\[|【|\()([^\]】\)]+)(?:\]|】|\))\s*");

        while (true)
        {
            var match = tagRegex.Match(text);
            if (!match.Success)
            {
                break;
            }

            var candidateTag = match.Groups[1].Value.Trim();
            if (IsRecognizedTag(candidateTag))
            {
                var previous = text;
                text = text[match.Length..].Trim();
                notes.Add($"Removed release tag '{candidateTag}'");
                transformations?.Add(new TitleTransformation("LeadingTag", $"Removed release tag '{candidateTag}'", previous, text));
            }
            else
            {
                // Arbitrary bracketed subtitle or unrecognized tag; leave intact
                break;
            }
        }

        return text;
    }

    private static string StripTrailingMetadata(
        string text,
        List<string> notes,
        List<TitleTransformation> transformations,
        List<string> extractedAliases)
    {
        var current = text;
        for (var i = 0; i < 5; i++)
        {
            var matched = false;

            // 1. Parenthesized recognized imprint/publisher (e.g. "(電撃文庫)", "（角川スニーカー文庫）")
            var imprintMatch = ParenthesizedImprintRegex.Match(current);
            if (imprintMatch.Success)
            {
                var candidateTag = imprintMatch.Groups[1].Value.Trim();
                if (IsRecognizedImprintOrPublisher(candidateTag))
                {
                    var previous = current;
                    current = CollapseWhitespace(current[..imprintMatch.Index]);
                    notes.Add($"Removed imprint suffix '{imprintMatch.Value.Trim()}'");
                    transformations.Add(new TitleTransformation("ImprintSuffix", $"Removed imprint suffix '{candidateTag}'", previous, current));
                    matched = true;
                    continue;
                }
            }

            // 2. Trailing Japanese series quote: 『...』シリーズ
            var seriesMatch = RetailerSeriesRegex.Match(current);
            if (seriesMatch.Success)
            {
                var seriesName = seriesMatch.Groups[1].Value.Trim();
                var previous = current;
                current = CollapseWhitespace(current[..seriesMatch.Index]);
                if (!string.IsNullOrWhiteSpace(seriesName))
                {
                    extractedAliases.Add(seriesName);
                }
                notes.Add($"Removed trailing series annotation '{seriesMatch.Value.Trim()}'");
                transformations.Add(new TitleTransformation("SeriesAnnotation", $"Removed trailing series annotation '{seriesName}'", previous, current));
                matched = true;
                continue;
            }

            // 3. Trailing angle-bracket retailer series annotation: <...> or 〈...〉
            var angleMatch = RetailerAngleBracketRegex.Match(current);
            if (angleMatch.Success)
            {
                var innerTitle = (angleMatch.Groups[1].Success ? angleMatch.Groups[1].Value :
                                  angleMatch.Groups[2].Success ? angleMatch.Groups[2].Value :
                                  angleMatch.Groups[3].Value).Trim();
                var previous = current;
                current = CollapseWhitespace(current[..angleMatch.Index]);
                if (!string.IsNullOrWhiteSpace(innerTitle))
                {
                    extractedAliases.Add(innerTitle);
                }
                notes.Add($"Removed retailer series annotation '{angleMatch.Value.Trim()}'");
                transformations.Add(new TitleTransformation("RetailerAnnotation", $"Removed retailer series annotation '{innerTitle}'", previous, current));
                matched = true;
                continue;
            }

            // 4. Trailing edition extras: 【ドラマＣＤ音源付き】, 【特装版】, etc.
            var editionMatch = EditionExtraRegex.Match(current);
            if (editionMatch.Success)
            {
                var extraTag = editionMatch.Groups[1].Value.Trim();
                if (IsRecognizedEditionExtra(extraTag))
                {
                    var previous = current;
                    current = CollapseWhitespace(current[..editionMatch.Index]);
                    notes.Add($"Removed edition extra '{editionMatch.Value.Trim()}'");
                    transformations.Add(new TitleTransformation("EditionExtra", $"Removed edition extra '{extraTag}'", previous, current));
                    matched = true;
                    continue;
                }
            }

            if (!matched)
            {
                break;
            }
        }

        return current;
    }

    private static bool IsRecognizedImprintOrPublisher(string tag)
    {
        if (IsRecognizedTag(tag))
        {
            return true;
        }

        return tag.EndsWith("文庫", StringComparison.Ordinal) ||
               tag.EndsWith("ノベルス", StringComparison.Ordinal) ||
               tag.EndsWith("ノベル", StringComparison.Ordinal) ||
               tag.EndsWith("ブックス", StringComparison.Ordinal) ||
               tag.EndsWith("BOOKS", StringComparison.OrdinalIgnoreCase) ||
               tag.EndsWith("コミックス", StringComparison.Ordinal);
    }

    private static bool IsRecognizedEditionExtra(string tag)
    {
        return Regex.IsMatch(tag, @"(?:ドラマ\s*CD|ドラマ\s*ＣＤ|音源|特装版|限定版|特典|イラスト|小冊子|Blu-ray|BD|DVD|音源付き|付き|限定|特装)");
    }

    private static (string? Number, string? BaseTitle) DetectAttachedAsciiTerminalNumber(string text)
    {
        var trimmed = text.Trim();
        if (Regex.IsMatch(trimmed, @"^\d+(?:\.\d+)?$"))
        {
            return (null, null); // all-numeric title (like "86")
        }

        var match = Regex.Match(trimmed, @"(?<=[^\s\d])(\d+(?:\.\d+)?)$");
        if (!match.Success)
        {
            return (null, null);
        }

        var numberStr = match.Groups[1].Value;
        var baseStr = CollapseWhitespace(trimmed[..match.Index]);
        if (string.IsNullOrWhiteSpace(baseStr))
        {
            return (null, null);
        }

        return (numberStr, baseStr);
    }

    public static SeriesQualifier ExtractSeriesQualifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SeriesQualifier.Mainline;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);

        // ArtBook
        if (Regex.IsMatch(normalized, @"(?:Art\s*Works|ArtWorks|画集|イラスト集|Fan\s*Book|ファンブック|公式ファンブック|ガイドブック)", RegexOptions.IgnoreCase))
        {
            return SeriesQualifier.ArtBook;
        }

        // ShortStories
        if (Regex.IsMatch(normalized, @"(?:短編集|短篇集|\bSS\b|Short\s*Stories)", RegexOptions.IgnoreCase))
        {
            return SeriesQualifier.ShortStories;
        }

        // Ex
        if (Regex.IsMatch(normalized, @"(?<![a-zA-Z])(?:Ex|EX)(?![a-zA-Z])", RegexOptions.IgnoreCase))
        {
            return SeriesQualifier.Ex;
        }

        // OtherSpecial
        if (Regex.IsMatch(normalized, @"(?:\d+年生編|外伝|特別編)"))
        {
            return SeriesQualifier.OtherSpecial;
        }

        return SeriesQualifier.Mainline;
    }

    public static SeriesQualifier GetCandidateSeriesQualifier(JitenMatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.IsSubdeck)
        {
            var parentOrig = ExtractSeriesQualifier(candidate.ParentOriginalTitle);
            if (parentOrig != SeriesQualifier.Mainline) return parentOrig;

            var parentEng = ExtractSeriesQualifier(candidate.ParentEnglishTitle);
            if (parentEng != SeriesQualifier.Mainline) return parentEng;

            var parentRom = ExtractSeriesQualifier(candidate.ParentRomajiTitle);
            if (parentRom != SeriesQualifier.Mainline) return parentRom;
        }

        var orig = ExtractSeriesQualifier(candidate.OriginalTitle);
        if (orig != SeriesQualifier.Mainline) return orig;

        var eng = ExtractSeriesQualifier(candidate.EnglishTitle);
        if (eng != SeriesQualifier.Mainline) return eng;

        var rom = ExtractSeriesQualifier(candidate.RomajiTitle);
        if (rom != SeriesQualifier.Mainline) return rom;

        return SeriesQualifier.Mainline;
    }

    private static IReadOnlyList<string> GenerateSearchAliases(
        string baseTitle,
        string canonicalBase,
        SeriesQualifier seriesQualifier,
        VolumeInferenceKind volumeInference,
        List<string> extractedAliases)
    {
        var aliases = new List<string>();

        if (volumeInference == VolumeInferenceKind.AttachedAsciiHypothesis)
        {
            aliases.Add(canonicalBase);
            aliases.Add(baseTitle);
        }
        else
        {
            aliases.Add(baseTitle);

            if (seriesQualifier == SeriesQualifier.ShortStories && !baseTitle.Contains("短編集") && !baseTitle.Contains("短篇集"))
            {
                aliases.Add($"{baseTitle} 短編集");
            }
            else if (seriesQualifier == SeriesQualifier.Ex && !Regex.IsMatch(baseTitle, @"\bEX\b|\bEx\b", RegexOptions.IgnoreCase))
            {
                aliases.Add($"{baseTitle} Ex");
            }
        }

        foreach (var extra in extractedAliases)
        {
            if (!string.IsNullOrWhiteSpace(extra) && !aliases.Contains(extra, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(extra);
            }
        }

        return aliases
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    private static bool IsRecognizedTag(string tag)
    {
        if (RecognizedTags.Contains(tag))
        {
            return true;
        }

        // Check if tag contains recognized publisher/format keyword
        foreach (var recognized in RecognizedTags)
        {
            if (tag.Equals(recognized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string BaseTitle, StructuredVolume? Volume) ExtractVolumeMarker(
        string text,
        List<string> notes,
        string? attachedFullWidthDigits = null)
    {
        // If the entire text consists of digits (e.g. "86"), never interpret it as a volume marker
        if (Regex.IsMatch(text, @"^\d+(?:\.\d+)?$"))
        {
            notes.Add($"Retained numeric title '{text}' as base title");
            return (text, null);
        }

        // 1. Special: School-year / Arc markers (e.g. "ようこそ実力至上主義の教室へ 2年生編 1")
        var arcMatch = Regex.Match(text, @"(?:\s+|^)(\d+年生編)\s*(\d+(?:\.\d+)?)$");
        if (arcMatch.Success && TryExtractRemaining(text, arcMatch.Index, out var remainingArc))
        {
            var tag = arcMatch.Groups[1].Value;
            if (decimal.TryParse(arcMatch.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var num))
            {
                notes.Add($"Extracted special arc volume '{tag} {num}'");
                return (remainingArc, StructuredVolume.Special(tag, num, arcMatch.Value.Trim()));
            }
            notes.Add($"Oversized arc volume number in '{arcMatch.Value.Trim()}' retained in base title");
        }

        // 2. Special: Episode markers (e.g. "Ep.1", "Ep. 1", "Episode 1")
        var epMatch = Regex.Match(text, @"(?:\s+|^)(?:Ep\.?|Episode\.?)\s*(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
        if (epMatch.Success && TryExtractRemaining(text, epMatch.Index, out var remainingEp))
        {
            if (decimal.TryParse(epMatch.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var num))
            {
                notes.Add($"Extracted special episode volume 'Episode {num}'");
                return (remainingEp, StructuredVolume.Special("Episode", num, epMatch.Value.Trim()));
            }
            notes.Add($"Oversized episode number in '{epMatch.Value.Trim()}' retained in base title");
        }

        // 3. Special: EX markers (e.g. "EX", "EX 1", "Ex.1", "Ex3　剣鬼恋譚", "Ex")
        var exMatch = Regex.Match(text, @"(?:\s+|^|(?<=[^\s\d]))(?:EX|Ex\.?)(?:\s*(\d+(?:\.\d+)?))?(?:\s+(.+))?$", RegexOptions.IgnoreCase);
        if (exMatch.Success && TryExtractRemaining(text, exMatch.Index, out var remainingEx))
        {
            if (!exMatch.Groups[1].Success)
            {
                notes.Add($"Extracted special EX volume '{exMatch.Value.Trim()}'");
                return (remainingEx, StructuredVolume.Special("EX", null, exMatch.Value.Trim()));
            }

            if (decimal.TryParse(exMatch.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var num))
            {
                notes.Add($"Extracted special EX volume '{exMatch.Value.Trim()}'");
                return (remainingEx, StructuredVolume.Special("EX", num, exMatch.Value.Trim()));
            }
            notes.Add($"Oversized EX volume number in '{exMatch.Value.Trim()}' retained in base title");
        }

        // 4. Special: Short-story markers (e.g. "短編集 1", "短編集", "SS 1", "短編集２")
        var ssMatch = Regex.Match(text, @"(?:\s+|^|(?<=[^\s\d]))(?:短編集|短篇集|SS|Short\s+Stories)(?:\s*(\d+(?:\.\d+)?))?(?:\s+(.+))?$", RegexOptions.IgnoreCase);
        if (ssMatch.Success && TryExtractRemaining(text, ssMatch.Index, out var remainingSs))
        {
            if (!ssMatch.Groups[1].Success)
            {
                notes.Add($"Extracted special short stories volume '{ssMatch.Value.Trim()}'");
                return (remainingSs, StructuredVolume.Special("ShortStories", null, ssMatch.Value.Trim()));
            }

            if (decimal.TryParse(ssMatch.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var num))
            {
                notes.Add($"Extracted special short stories volume '{ssMatch.Value.Trim()}'");
                return (remainingSs, StructuredVolume.Special("ShortStories", num, ssMatch.Value.Trim()));
            }
            notes.Add($"Oversized short stories volume number in '{ssMatch.Value.Trim()}' retained in base title");
        }

        // 5. Japanese Position Marker (上 / 中 / 下)
        var posMatch = Regex.Match(text, @"(?:\s*[\(（]([上中下])[\)）]|\s+([上中下]))$");
        if (posMatch.Success && TryExtractRemaining(text, posMatch.Index, out var remainingPos))
        {
            var charStr = posMatch.Groups[1].Success ? posMatch.Groups[1].Value : posMatch.Groups[2].Value;
            var pos = charStr switch
            {
                "上" => PositionMarker.Upper,
                "中" => PositionMarker.Middle,
                "下" => PositionMarker.Lower,
                _ => (PositionMarker?)null
            };

            if (pos.HasValue)
            {
                notes.Add($"Extracted Japanese position volume '{charStr}'");
                return (remainingPos, StructuredVolume.FromPosition(pos.Value, charStr));
            }
        }

        // 6. Explicit Japanese volume marker: 第…巻 (e.g. "第1巻", "第01巻", "第4.5巻", "作品第1巻")
        var daiKanMatch = Regex.Match(text, @"(?:\s*|^)第\s*(\d+(?:\.\d+)?)\s*巻$");
        if (daiKanMatch.Success && TryExtractRemaining(text, daiKanMatch.Index, out var remainingDaiKan))
        {
            var vol = TryParseNumberVolume(daiKanMatch.Groups[1].Value, daiKanMatch.Value.Trim(), notes);
            if (vol is not null)
            {
                return (remainingDaiKan, vol);
            }
        }

        // 7. Explicit Japanese volume marker: …巻 (e.g. "1巻", "01巻", "14巻", "4.5巻", "作品1巻")
        var kanMatch = Regex.Match(text, @"(?:\s*|^)(\d+(?:\.\d+)?)\s*巻$");
        if (kanMatch.Success && TryExtractRemaining(text, kanMatch.Index, out var remainingKan))
        {
            var vol = TryParseNumberVolume(kanMatch.Groups[1].Value, kanMatch.Value.Trim(), notes);
            if (vol is not null)
            {
                return (remainingKan, vol);
            }
        }

        // 8. Explicit Latin volume prefix: Vol. / Volume with Arabic or decimal (e.g. "Vol. 1", "Volume 14", "Vol. 4.5")
        var volArabicMatch = Regex.Match(text, @"(?:\s+|^)(?:vol\.|volume|vol|v)\s*(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
        if (volArabicMatch.Success && TryExtractRemaining(text, volArabicMatch.Index, out var remainingVolArabic))
        {
            var vol = TryParseNumberVolume(volArabicMatch.Groups[1].Value, volArabicMatch.Value.Trim(), notes);
            if (vol is not null)
            {
                return (remainingVolArabic, vol);
            }
        }

        // 9. Explicit Latin volume prefix with Roman numerals: Vol. IV, Volume II, Vol IX
        var volRomanMatch = Regex.Match(text, @"(?:\s+|^)(?:vol\.|volume|vol)\s*([IVXLCDM]+)$", RegexOptions.IgnoreCase);
        if (volRomanMatch.Success && TryExtractRemaining(text, volRomanMatch.Index, out var remainingVolRoman))
        {
            var romanStr = volRomanMatch.Groups[1].Value;
            if (TryParseRomanNumeral(romanStr, out var romanInt))
            {
                notes.Add($"Extracted explicit Roman numeral volume {romanInt} from '{volRomanMatch.Value.Trim()}'");
                return (remainingVolRoman, StructuredVolume.Standard(romanInt, volRomanMatch.Value.Trim()));
            }
        }

        // 10. Parenthesized number at end (e.g. "(14)", "(01)", "（1）")
        var parenMatch = Regex.Match(text, @"(?:\s*[\(（](\d+(?:\.\d+)?)[\)）])$");
        if (parenMatch.Success && TryExtractRemaining(text, parenMatch.Index, out var remainingParen))
        {
            var vol = TryParseNumberVolume(parenMatch.Groups[1].Value, parenMatch.Value.Trim(), notes);
            if (vol is not null)
            {
                return (remainingParen, vol);
            }
        }

        // 11. Bare terminal Arabic number preceded by whitespace (e.g. " 01", " 14", " 4.5")
        var bareMatch = Regex.Match(text, @"\s+(\d+(?:\.\d+)?)$");
        if (bareMatch.Success && TryExtractRemaining(text, bareMatch.Index, out var remainingBare))
        {
            var vol = TryParseNumberVolume(bareMatch.Groups[1].Value, bareMatch.Value.Trim(), notes);
            if (vol is not null)
            {
                return (remainingBare, vol);
            }
        }

        // 12. Attached full-width terminal number (e.g. "Ｒｅ：ゼロから始める異世界生活１")
        // Only enabled when pre-NFKC inspection verified the suffix was originally full-width digits.
        if (attachedFullWidthDigits is not null)
        {
            var attachedMatch = Regex.Match(text, @"(?<=[^\s\d])(\d+(?:\.\d+)?)$");
            if (attachedMatch.Success && TryExtractRemaining(text, attachedMatch.Index, out var remainingAttached))
            {
                var vol = TryParseNumberVolume(attachedMatch.Groups[1].Value, attachedFullWidthDigits, notes);
                if (vol is not null)
                {
                    notes.Add($"Extracted attached full-width volume '{attachedFullWidthDigits}'");
                    return (remainingAttached, vol);
                }
            }
        }

        // Check bare Roman numeral (e.g. "狼と香辛料 I"):
        // Do NOT treat as volume! Preserved as base title.
        var bareRoman = Regex.Match(text, @"\s+([IVXLCDM]+)$", RegexOptions.IgnoreCase);
        if (bareRoman.Success)
        {
            notes.Add($"Retained bare terminal Roman numeral '{bareRoman.Groups[1].Value}' as part of base title");
        }

        return (CollapseWhitespace(text), null);
    }

    private static bool TryExtractRemaining(string text, int matchIndex, out string remaining)
    {
        remaining = CollapseWhitespace(text[..matchIndex]);
        return !string.IsNullOrWhiteSpace(remaining);
    }

    private static StructuredVolume? TryParseNumberVolume(string rawNumber, string rawMarker, List<string> notes)
    {
        if (rawNumber.Contains('.'))
        {
            if (decimal.TryParse(rawNumber, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dec))
            {
                notes.Add($"Extracted fractional volume {dec} from '{rawMarker}'");
                return StructuredVolume.Fractional(dec, rawMarker);
            }

            notes.Add($"Oversized fractional volume '{rawMarker}' retained in base title");
            return null;
        }

        if (int.TryParse(rawNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
        {
            notes.Add($"Extracted volume {num} from '{rawMarker}'");
            return StructuredVolume.Standard(num, rawMarker);
        }

        notes.Add($"Oversized volume number '{rawMarker}' retained in base title");
        return null;
    }

    private static bool TryParseRomanNumeral(string input, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var upper = input.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(upper, "^M{0,4}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$") || upper.Length == 0)
        {
            return false;
        }

        var total = 0;
        for (var i = 0; i < upper.Length; i++)
        {
            if (!RomanMap.TryGetValue(upper[i], out var current))
            {
                return false;
            }

            if (i + 1 < upper.Length && RomanMap.TryGetValue(upper[i + 1], out var next) && current < next)
            {
                total -= current;
            }
            else
            {
                total += current;
            }
        }

        result = total;
        return total > 0;
    }

    private static string CollapseWhitespace(string text)
    {
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
