using WordLinkSolver.Core;
using Xunit;

namespace WordLinkSolver.Tests;

public class OcrNormalizerTests
{
    [Fact]
    public void UppercaseLettersPassThroughClean()
    {
        var result = OcrNormalizer.NormalizeChar('M');
        Assert.Equal(('M', false), result);
    }

    [Fact]
    public void LowercaseIsUppercasedWithoutPenalty()
    {
        var result = OcrNormalizer.NormalizeChar('q');
        Assert.Equal(('Q', false), result);
    }

    [Theory]
    [InlineData('0', 'O')]
    [InlineData('1', 'I')]
    [InlineData('|', 'I')]
    [InlineData('5', 'S')]
    [InlineData('8', 'B')]
    [InlineData('2', 'Z')]
    public void CommonLookalikesAreRemapped(char input, char expected)
    {
        var result = OcrNormalizer.NormalizeChar(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value.Letter);
        Assert.True(result.Value.Remapped);
    }

    [Theory]
    [InlineData('.')]
    [InlineData(' ')]
    [InlineData('~')]
    public void NoiseCharactersAreRejected(char input)
    {
        Assert.Null(OcrNormalizer.NormalizeChar(input));
    }

    [Fact]
    public void CleanSingleCharTokenHasFullConfidence()
    {
        var result = OcrNormalizer.NormalizeToken("M");
        Assert.Equal('M', result!.Value.Letter);
        Assert.Equal(OcrNormalizer.CleanConfidence, result.Value.Confidence);
    }

    [Fact]
    public void RemappedTokenHasReducedConfidence()
    {
        var result = OcrNormalizer.NormalizeToken("|");
        Assert.Equal('I', result!.Value.Letter);
        Assert.True(result.Value.Confidence < 0.6f);
    }

    [Fact]
    public void MultiCharTokenPrefersTheCleanLetterAndIsPenalized()
    {
        var result = OcrNormalizer.NormalizeToken("!M");
        Assert.Equal('M', result!.Value.Letter);
        Assert.True(result.Value.Confidence < OcrNormalizer.CleanConfidence);
        Assert.True(result.Value.Confidence >= 0.5f);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void EmptyOrNoiseTokensReturnNull(string? token)
    {
        Assert.Null(OcrNormalizer.NormalizeToken(token));
    }

    [Fact]
    public void TokenWithTrailingDotKeepsTheLetter()
    {
        // The point-value dots under a tile sometimes leak into the OCR crop.
        var result = OcrNormalizer.NormalizeToken("N.");
        Assert.Equal('N', result!.Value.Letter);
    }
}
