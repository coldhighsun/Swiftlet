using AwesomeAssertions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Domain.Tests;

/// <summary>
/// Tests for <see cref="AccountColors"/> and the color of a <see cref="MailAccount"/>.
/// </summary>
public class AccountColorTests
{
    /// <summary>
    /// Creates an account.
    /// </summary>
    /// <param name="color">The color, if one is chosen.</param>
    /// <returns>The account.</returns>
    private static MailAccount Account(string? color = null)
    {
        return MailAccount.Create(
            "Me",
            "me@example.com",
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UnixEpoch,
            color: color);
    }

    /// <summary>
    /// Verifies derived colors stay what earlier versions showed, so existing accounts do not change.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="expected">The color shown for it before colors could be chosen.</param>
    [Theory]
    [InlineData("me@example.com", "#3F7D3A")]
    [InlineData(" ME@Example.com ", "#3F7D3A")]
    [InlineData("", "#9A6400")]
    public void Derive_Address_KeepsTheEarlierColor(string address, string expected)
    {
        AccountColors.Derive(address).Should().Be(expected);
    }

    /// <summary>
    /// Verifies the palette offers distinct colors that include every derived one.
    /// </summary>
    [Fact]
    public void Palette_ContainsDistinctColorsIncludingTheDerivedOnes()
    {
        AccountColors.Palette.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        AccountColors.Palette.Should().Contain(AccountColors.Derivable);
    }

    /// <summary>
    /// Verifies the only color left is picked, whatever the case of the used ones.
    /// </summary>
    [Fact]
    public void PickUnused_OneColorFree_PicksIt()
    {
        var used = AccountColors.Palette.Skip(1).Select(c => c.ToLowerInvariant());

        var color = AccountColors.PickUnused(used, new Random(1));

        color.Should().Be(AccountColors.Palette[0]);
    }

    /// <summary>
    /// Verifies the pick is random among the free colors.
    /// </summary>
    [Fact]
    public void PickUnused_SeveralColorsFree_VariesAndAvoidsUsedOnes()
    {
        var random = new Random(1);
        var used = AccountColors.Palette.Take(2).ToList();

        var picks = Enumerable.Range(0, 200).Select(_ => AccountColors.PickUnused(used, random)).ToHashSet();

        picks.Should().BeEquivalentTo(AccountColors.Palette.Skip(2));
    }

    /// <summary>
    /// Verifies a color used least is picked once every color is in use.
    /// </summary>
    [Fact]
    public void PickUnused_AllUsed_PicksOneUsedLeast()
    {
        var used = AccountColors.Palette.Concat(AccountColors.Palette.Where((_, i) => i != 4));

        var color = AccountColors.PickUnused(used, new Random(1));

        color.Should().Be(AccountColors.Palette[4]);
    }

    /// <summary>
    /// Verifies an account without a chosen color shows the one of its address until one is chosen.
    /// </summary>
    [Fact]
    public void ChangeColor_NoColorBefore_ReplacesTheDerivedOne()
    {
        var account = Account();
        account.Color.Should().BeNull();
        account.EffectiveColor.Should().Be(AccountColors.Derive(account.EmailAddress));

        account.ChangeColor("#C2185B");

        account.Color.Should().Be("#C2185B");
        account.EffectiveColor.Should().Be("#C2185B");
    }

    /// <summary>
    /// Verifies a color given at creation is kept.
    /// </summary>
    [Fact]
    public void Create_WithColor_KeepsIt()
    {
        Account("#6B6F00").EffectiveColor.Should().Be("#6B6F00");
    }

    /// <summary>
    /// Verifies only <c>#RRGGBB</c> colors are accepted.
    /// </summary>
    /// <param name="color">The rejected value.</param>
    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    public void ChangeColor_Invalid_Throws(string color)
    {
        var account = Account();

        var change = () => account.ChangeColor(color);
        var create = () => Account(color);

        change.Should().Throw<ArgumentException>();
        create.Should().Throw<ArgumentException>();
        account.Color.Should().BeNull();
    }
}
