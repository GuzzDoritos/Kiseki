using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Tests;

public sealed class IsbnValidatorTests
{
    [Theory]
    [InlineData("978-4-04-893341-4", "9784048933414")]
    [InlineData("9784048933414", "9784048933414")]
    [InlineData("979-10-90636-07-1", "9791090636071")]
    [InlineData("  978 4 04 893341 4  ", "9784048933414")]
    public void NormalizeAndValidateIsbn_ValidIsbn13_ReturnsNormalizedString(string raw, string expected)
    {
        var result = IsbnValidator.NormalizeAndValidateIsbn(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0-306-40615-2", "9780306406157")] // Known ISBN-10 to ISBN-13
    [InlineData("0306406152", "9780306406157")]
    [InlineData("4-04-893341-0", null)] // Invalid check digit for 10
    [InlineData("0-8044-2957-X", "9780804429573")] // Check digit 'X'
    [InlineData("080442957x", "9780804429573")] // Lowercase 'x'
    public void NormalizeAndValidateIsbn_ValidIsbn10_ConvertsToIsbn13(string raw, string? expected)
    {
        var result = IsbnValidator.NormalizeAndValidateIsbn(raw);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-an-isbn")]
    [InlineData("12345")]
    [InlineData("9784048933415")] // Bad check digit (should be 4)
    [InlineData("978404893341X")] // 'X' not allowed in ISBN-13
    [InlineData("9704048933414")] // Invalid prefix (neither 978 nor 979)
    [InlineData("0306406153")] // Bad check digit in 10
    public void NormalizeAndValidateIsbn_InvalidInputs_ReturnsNull(string? raw)
    {
        var result = IsbnValidator.NormalizeAndValidateIsbn(raw);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAndNormalizeIsbn_PrefersIsbn13OverIsbn10()
    {
        var identifiers = new List<GoogleBooksIndustryIdentifierDto>
        {
            new("ISBN_10", "0306406152"),
            new("ISBN_13", "9784048933414")
        };

        var result = IsbnValidator.ExtractAndNormalizeIsbn(identifiers);
        Assert.Equal("9784048933414", result);
    }

    [Fact]
    public void ExtractAndNormalizeIsbn_FallsBackToIsbn10WhenIsbn13Invalid()
    {
        var identifiers = new List<GoogleBooksIndustryIdentifierDto>
        {
            new("ISBN_13", "invalid13"),
            new("ISBN_10", "0306406152")
        };

        var result = IsbnValidator.ExtractAndNormalizeIsbn(identifiers);
        Assert.Equal("9780306406157", result);
    }

    [Fact]
    public void ExtractAndNormalizeIsbn_NullOrEmptyIdentifiers_ReturnsNull()
    {
        Assert.Null(IsbnValidator.ExtractAndNormalizeIsbn(null));
        Assert.Null(IsbnValidator.ExtractAndNormalizeIsbn([]));
    }
}

