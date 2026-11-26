using AwesomeAssertions;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="RemoteImageRule"/>.
/// </summary>
public class RemoteImageRuleTests
{
    /// <summary>
    /// Verifies valid addresses and domains are normalized to lower case without the decoration.
    /// </summary>
    /// <param name="input">The raw input.</param>
    /// <param name="expected">The expected rule.</param>
    [Theory]
    [InlineData("News@Shop.COM", "news@shop.com")]
    [InlineData("  news@shop.com  ", "news@shop.com")]
    [InlineData("shop.com", "shop.com")]
    [InlineData("Shop.Com", "shop.com")]
    [InlineData("@shop.com", "shop.com")]
    [InlineData("*.shop.com", "shop.com")]
    [InlineData("mail.shop.co.uk", "mail.shop.co.uk")]
    [InlineData("用户@例子.中国", "用户@例子.中国")]
    [InlineData("例子.中国", "例子.中国")]
    public void TryNormalize_ValidInput_ReturnsNormalizedRule(string input, string expected)
    {
        var valid = RemoteImageRule.TryNormalize(input, out var rule);

        valid.Should().BeTrue();
        rule.Should().Be(expected);
    }

    /// <summary>
    /// Verifies text that is neither an address nor a domain is rejected.
    /// </summary>
    /// <param name="input">The raw input.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("shop")]
    [InlineData("a@b")]
    [InlineData("@@shop.com")]
    [InlineData("two words.com")]
    [InlineData("a b@shop.com")]
    [InlineData("a@b@shop.com")]
    [InlineData("shop..com")]
    [InlineData("http://shop.com")]
    [InlineData("shop.com/path")]
    public void TryNormalize_InvalidInput_ReturnsFalse(string? input)
    {
        var valid = RemoteImageRule.TryNormalize(input, out var rule);

        valid.Should().BeFalse();
        rule.Should().BeNull();
    }

    /// <summary>
    /// Verifies the candidates are the address followed by the domain and its parents, without the top-level domain.
    /// </summary>
    [Fact]
    public void Candidates_AddressWithSubdomain_ListsAddressDomainAndParents()
    {
        RemoteImageRule.Candidates("News@Mail.Shop.com").Should().Equal("news@mail.shop.com", "mail.shop.com", "shop.com");
    }

    /// <summary>
    /// Verifies a two-label domain has itself as the only domain candidate.
    /// </summary>
    [Fact]
    public void Candidates_SimpleDomain_ListsAddressAndDomain()
    {
        RemoteImageRule.Candidates("a@shop.com").Should().Equal("a@shop.com", "shop.com");
    }

    /// <summary>
    /// Verifies unusable sender addresses have no candidates, so no rule can ever match them.
    /// </summary>
    /// <param name="address">The sender address.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown@invalid")]
    [InlineData("not-an-address")]
    [InlineData("shop.com")]
    public void Candidates_UnusableAddress_ReturnsEmpty(string? address)
    {
        RemoteImageRule.Candidates(address).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a domain rule does not match a different domain that merely ends with the same letters.
    /// </summary>
    [Fact]
    public void Candidates_LookalikeDomain_DoesNotContainTheShorterDomain()
    {
        RemoteImageRule.Candidates("a@evilshop.com").Should().NotContain("shop.com");
    }
}
