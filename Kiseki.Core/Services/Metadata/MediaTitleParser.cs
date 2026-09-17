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

        // 1. NFKC normalization and whitespace collapse
        var normalized = rawTitle.Normalize(NormalizationForm.FormKC);
        var collapsed = CollapseWhitespace(normalized);
        var comparisonTitle = collapsed;

        var current = collapsed;

        // 2. Strip recognized file suffixes (.epub, .html, .txt)
        foreach (var suffix in FileSuffixes)
        {
            if (current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                current = current[..^suffix.Length].Trim();
                notes.Add($"Removed file suffix '{suffix}'");
                break;
            }
        }

        // 3. Strip recognized leading bracketed release/publisher tags
        current = StripLeadingTags(current, notes);

        // 4. Extract terminal volume marker
        var (baseTitle, volume) = ExtractVolumeMarker(current, notes);

        return new ParsedMediaTitle
        {
            OriginalTitle = rawTitle,
            ComparisonTitle = comparisonTitle,
            BaseTitle = baseTitle,
            Volume = volume,
            ParsingNotes = notes
        };
    }

    private static string StripLeadingTags(string text, List<string> notes)
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
                text = text[match.Length..].Trim();
                notes.Add($"Removed release tag '{candidateTag}'");
            }
            else
            {
                // Arbitrary bracketed subtitle or unrecognized tag; leave intact
                break;
            }
        }

        return text;
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
        List<string> notes)
    {
        // If the entire text consists of digits (e.g. "86"), never interpret it as a volume marker
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
        {
            notes.Add($"Retained numeric title '{text}' as base title");
            return (text, null);
        }

        // 1. Special: School-year / Arc markers (e.g. "ようこそ実力至上主義の教室へ 2年生編 1")
        var arcMatch = Regex.Match(text, @"(?:\s+|^)(\d+年生編)\s*(\d+(?:\.\d+)?)$");
        if (arcMatch.Success && TryExtractRemaining(text, arcMatch.Index, out var remainingArc))
        {
            var tag = arcMatch.Groups[1].Value;
            var num = decimal.Parse(arcMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            notes.Add($"Extracted special arc volume '{tag} {num}'");
            return (remainingArc, StructuredVolume.Special(tag, num, arcMatch.Value.Trim()));
        }

        // 2. Special: Episode markers (e.g. "Ep.1", "Ep. 1", "Episode 1")
        var epMatch = Regex.Match(text, @"(?:\s+|^)(?:Ep\.?|Episode\.?)\s*(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
        if (epMatch.Success && TryExtractRemaining(text, epMatch.Index, out var remainingEp))
        {
            var num = decimal.Parse(epMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            notes.Add($"Extracted special episode volume 'Episode {num}'");
            return (remainingEp, StructuredVolume.Special("Episode", num, epMatch.Value.Trim()));
        }

        // 3. Special: EX markers (e.g. "EX", "EX 1", "Ex.1")
        var exMatch = Regex.Match(text, @"(?:\s+|^)(?:EX|Ex\.?)(?:\s*(\d+(?:\.\d+)?))?$", RegexOptions.IgnoreCase);
        if (exMatch.Success && TryExtractRemaining(text, exMatch.Index, out var remainingEx))
        {
            decimal? num = exMatch.Groups[1].Success
                ? decimal.Parse(exMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : null;
            notes.Add($"Extracted special EX volume '{exMatch.Value.Trim()}'");
            return (remainingEx, StructuredVolume.Special("EX", num, exMatch.Value.Trim()));
        }

        // 4. Special: Short-story markers (e.g. "短編集 1", "短編集", "SS 1")
        var ssMatch = Regex.Match(text, @"(?:\s+|^)(?:短編集|短編|SS|Short\s+Stories)(?:\s*(\d+(?:\.\d+)?))?$", RegexOptions.IgnoreCase);
        if (ssMatch.Success && TryExtractRemaining(text, ssMatch.Index, out var remainingSs))
        {
            decimal? num = ssMatch.Groups[1].Success
                ? decimal.Parse(ssMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : null;
            notes.Add($"Extracted special short stories volume '{ssMatch.Value.Trim()}'");
            return (remainingSs, StructuredVolume.Special("ShortStories", num, ssMatch.Value.Trim()));
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

        // 6. Explicit Japanese volume marker: 第…巻 (e.g. "第1巻", "第01巻", "第4.5巻")
        var daiKanMatch = Regex.Match(text, @"(?:\s+|^)第\s*(\d+(?:\.\d+)?)\s*巻$");
        if (daiKanMatch.Success && TryExtractRemaining(text, daiKanMatch.Index, out var remainingDaiKan))
        {
            var rawNum = daiKanMatch.Groups[1].Value;
            var vol = ParseNumberVolume(rawNum, daiKanMatch.Value.Trim(), notes);
            return (remainingDaiKan, vol);
        }

        // 7. Explicit Japanese volume marker: …巻 (e.g. "1巻", "01巻", "14巻", "4.5巻")
        var kanMatch = Regex.Match(text, @"(?:\s+|^)(\d+(?:\.\d+)?)\s*巻$");
        if (kanMatch.Success && TryExtractRemaining(text, kanMatch.Index, out var remainingKan))
        {
            var rawNum = kanMatch.Groups[1].Value;
            var vol = ParseNumberVolume(rawNum, kanMatch.Value.Trim(), notes);
            return (remainingKan, vol);
        }

        // 8. Explicit Latin volume prefix: Vol. / Volume with Arabic or decimal (e.g. "Vol. 1", "Volume 14", "Vol. 4.5")
        var volArabicMatch = Regex.Match(text, @"(?:\s+|^)(?:vol\.|volume|vol|v)\s*(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
        if (volArabicMatch.Success && TryExtractRemaining(text, volArabicMatch.Index, out var remainingVolArabic))
        {
            var rawNum = volArabicMatch.Groups[1].Value;
            var vol = ParseNumberVolume(rawNum, volArabicMatch.Value.Trim(), notes);
            return (remainingVolArabic, vol);
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
            var rawNum = parenMatch.Groups[1].Value;
            var vol = ParseNumberVolume(rawNum, parenMatch.Value.Trim(), notes);
            return (remainingParen, vol);
        }

        // 11. Bare terminal Arabic number preceded by whitespace (e.g. " 01", " 14", " 4.5")
        var bareMatch = Regex.Match(text, @"\s+(\d+(?:\.\d+)?)$");
        if (bareMatch.Success && TryExtractRemaining(text, bareMatch.Index, out var remainingBare))
        {
            var rawNum = bareMatch.Groups[1].Value;
            var vol = ParseNumberVolume(rawNum, bareMatch.Value.Trim(), notes);
            return (remainingBare, vol);
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

    private static StructuredVolume ParseNumberVolume(string rawNumber, string rawMarker, List<string> notes)
    {
        if (rawNumber.Contains('.'))
        {
            var dec = decimal.Parse(rawNumber, CultureInfo.InvariantCulture);
            notes.Add($"Extracted fractional volume {dec} from '{rawMarker}'");
            return StructuredVolume.Fractional(dec, rawMarker);
        }

        var num = int.Parse(rawNumber, CultureInfo.InvariantCulture);
        notes.Add($"Extracted volume {num} from '{rawMarker}'");
        return StructuredVolume.Standard(num, rawMarker);
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
