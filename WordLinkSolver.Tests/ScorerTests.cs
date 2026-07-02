using WordLinkSolver.Core;
using Xunit;

namespace WordLinkSolver.Tests;

public class ScorerTests
{
    [Theory]
    [InlineData('A', 1)]
    [InlineData('E', 1)]
    [InlineData('D', 2)]
    [InlineData('B', 3)]
    [InlineData('F', 4)]
    [InlineData('K', 5)]
    [InlineData('J', 8)]
    [InlineData('X', 8)]
    [InlineData('Q', 10)]
    [InlineData('Z', 10)]
    public void LetterValuesMatchTheSpecTable(char letter, int expected)
    {
        Assert.Equal(expected, Scorer.LetterValue(letter));
    }

    [Fact]
    public void LowercaseLettersScoreLikeUppercase()
    {
        Assert.Equal(10, Scorer.LetterValue('q'));
    }

    [Fact]
    public void NonLettersScoreZero()
    {
        Assert.Equal(0, Scorer.LetterValue('\0'));
        Assert.Equal(0, Scorer.LetterValue('3'));
        Assert.Equal(0, Scorer.LetterValue(' '));
    }

    [Theory]
    [InlineData("CAB", 3 + 1 + 3)]
    [InlineData("QUIZ", 10 + 1 + 1 + 10)]
    [InlineData("MISCREANT", 3 + 1 + 1 + 3 + 1 + 1 + 1 + 1 + 1)]
    public void WordScoreIsTheSumOfLetterValues(string word, int expected)
    {
        Assert.Equal(expected, Scorer.Score(word));
    }
}
