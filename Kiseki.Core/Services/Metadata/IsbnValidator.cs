using Kiseki.Core.Models.GoogleBooks;

namespace Kiseki.Core.Services.Metadata;

public static class IsbnValidator
{
    public static string? ExtractAndNormalizeIsbn(IReadOnlyList<GoogleBooksIndustryIdentifierDto>? identifiers)
    {
        if (identifiers is null || identifiers.Count == 0)
        {
            return null;
        }

        var isbn13 = identifiers
            .FirstOrDefault(i => string.Equals(i.Type, "ISBN_13", StringComparison.OrdinalIgnoreCase))
            ?.Identifier;
        var validated13 = NormalizeAndValidateIsbn(isbn13);
        if (!string.IsNullOrWhiteSpace(validated13))
        {
            return validated13;
        }

        var isbn10 = identifiers
            .FirstOrDefault(i => string.Equals(i.Type, "ISBN_10", StringComparison.OrdinalIgnoreCase))
            ?.Identifier;
        return NormalizeAndValidateIsbn(isbn10);
    }

    public static string? NormalizeAndValidateIsbn(string? rawIsbn)
    {
        if (string.IsNullOrWhiteSpace(rawIsbn))
        {
            return null;
        }

        var clean = new string(rawIsbn.Where(c => char.IsAsciiDigit(c) || c is 'X' or 'x').ToArray()).ToUpperInvariant();

        // 13-digit ISBN
        if (clean.Length == 13 && (clean.StartsWith("978", StringComparison.Ordinal) || clean.StartsWith("979", StringComparison.Ordinal)))
        {
            if (!clean.All(char.IsAsciiDigit))
            {
                return null;
            }

            var sum = 0;
            for (var i = 0; i < 12; i++)
            {
                var digit = clean[i] - '0';
                sum += (i % 2 == 0) ? digit : digit * 3;
            }
            var check = (10 - (sum % 10)) % 10;
            if (clean[12] - '0' == check)
            {
                return clean;
            }

            return null;
        }

        // 10-digit ISBN -> convert to standard ISBN-13
        if (clean.Length == 10 && clean[..9].All(char.IsAsciiDigit))
        {
            var sum = 0;
            for (var i = 0; i < 9; i++)
            {
                sum += (clean[i] - '0') * (10 - i);
            }
            var checkCharVal = clean[9] is 'X' ? 10 : clean[9] - '0';
            sum += checkCharVal;
            if (sum % 11 == 0)
            {
                var core = "978" + clean[..9];
                var sum13 = 0;
                for (var i = 0; i < 12; i++)
                {
                    var digit = core[i] - '0';
                    sum13 += (i % 2 == 0) ? digit : digit * 3;
                }
                var check13 = (10 - (sum13 % 10)) % 10;
                return core + check13;
            }
        }

        return null;
    }
}
