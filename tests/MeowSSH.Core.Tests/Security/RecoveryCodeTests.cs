using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Security;

public class RecoveryCodeTests
{
    [Fact]
    public void GeneratedCodeIsGroupedAndWellFormed()
    {
        var code = RecoveryCode.Generate();

        Assert.Contains('-', code);
        Assert.True(RecoveryCode.IsWellFormed(code));
        Assert.All(code.Split('-')[..^1], group => Assert.Equal(4, group.Length));
    }

    [Fact]
    public void GeneratedCodesAreDistinct()
    {
        var codes = Enumerable.Range(0, 64).Select(_ => RecoveryCode.Generate()).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void CodeCarriesTheFullEntropyItClaims()
    {
        using var entropy = RecoveryCode.Parse(RecoveryCode.Generate());
        Assert.Equal(20, entropy.Length);   // 160 bits
    }

    [Fact]
    public void GeneratedCodeAvoidsTheCharactersPeopleMisread()
    {
        // Crockford Base32 drops I, L, O and U so a code copied onto paper and
        // typed back cannot be ambiguous.
        var codes = string.Concat(Enumerable.Range(0, 200).Select(_ => RecoveryCode.Generate()));
        Assert.DoesNotContain('I', codes);
        Assert.DoesNotContain('L', codes);
        Assert.DoesNotContain('O', codes);
        Assert.DoesNotContain('U', codes);
    }

    [Theory]
    [InlineData("lower case")]
    [InlineData("spaces not dashes")]
    [InlineData("no separators")]
    [InlineData("letter oh for zero")]
    [InlineData("letter ell for one")]
    public void CodeSurvivesHowPeopleActuallyTypeItBack(string scenario)
    {
        var code = RecoveryCode.Generate();
        using var expected = RecoveryCode.Parse(code);

        var retyped = scenario switch
        {
            "lower case" => code.ToLowerInvariant(),
            "spaces not dashes" => code.Replace('-', ' '),
            "no separators" => code.Replace("-", ""),
            "letter oh for zero" => code.Replace('0', 'O'),
            "letter ell for one" => code.Replace('1', 'L'),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using var actual = RecoveryCode.Parse(retyped);
        Assert.Equal(expected.ReadOnlySpan.ToArray(), actual.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void DifferentCodesDecodeToDifferentEntropy()
    {
        using var first = RecoveryCode.Parse(RecoveryCode.Generate());
        using var second = RecoveryCode.Parse(RecoveryCode.Generate());
        Assert.NotEqual(first.ReadOnlySpan.ToArray(), second.ReadOnlySpan.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCD-EFGH")]                        // too short
    [InlineData("ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789-EXTRA")]  // too long
    public void MalformedCodesAreRejected(string code)
    {
        Assert.False(RecoveryCode.IsWellFormed(code));
    }

    [Fact]
    public void CharactersOutsideTheAlphabetAreRejected()
    {
        Assert.Throws<FormatException>(() => RecoveryCode.Parse("ABCD-EFGH-JKMN-PQR!"));
    }

    [Fact]
    public void NullOrBlankIsRejected()
    {
        Assert.False(RecoveryCode.IsWellFormed(null));
        Assert.Throws<ArgumentException>(() => RecoveryCode.Parse("  "));
    }
}
