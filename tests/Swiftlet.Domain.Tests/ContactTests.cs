using AwesomeAssertions;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Domain.Tests;

/// <summary>
/// Tests <see cref="Contact"/>.
/// </summary>
public sealed class ContactTests
{
    /// <summary>
    /// Verifies values are trimmed and blank or repeated entries dropped.
    /// </summary>
    [Fact]
    public void Create_UntidyValues_CleansThem()
    {
        var contact = Contact.Create(
            " 三 ", " 张 ", [" a@x.com ", "", "A@X.COM", "b@x.com"], ["123", " 123 ", " "], " Acme ", " note ");

        contact.FirstName.Should().Be("三");
        contact.LastName.Should().Be("张");
        contact.DisplayName.Should().Be("张三");
        contact.Emails.Should().Equal("a@x.com", "b@x.com");
        contact.Phones.Should().Equal("123");
        contact.Company.Should().Be("Acme");
        contact.Note.Should().Be("note");
    }

    /// <summary>
    /// Verifies a contact cannot be created without an address.
    /// </summary>
    [Fact]
    public void Create_NoEmail_Throws()
    {
        var act = () => Contact.Create("Name", null, [" "]);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies a rejected update leaves the contact unchanged.
    /// </summary>
    [Fact]
    public void Update_InvalidEmail_ThrowsAndKeepsValues()
    {
        var contact = Contact.Create("Name", null, ["a@x.com"]);

        var act = () => contact.Update("Other", null, ["nope"]);

        act.Should().Throw<ArgumentException>();
        contact.DisplayName.Should().Be("Name");
        contact.Emails.Should().Equal("a@x.com");
    }

    /// <summary>
    /// Verifies the order and spacing of a composed name.
    /// </summary>
    /// <param name="first">The given name.</param>
    /// <param name="last">The family name.</param>
    /// <param name="expected">The display name.</param>
    [Theory]
    [InlineData("John", "Doe", "John Doe")]
    [InlineData("三", "张", "张三")]
    [InlineData("John", "", "John")]
    [InlineData("", "张", "张")]
    [InlineData("", "", "")]
    public void Compose_Parts_ReturnsDisplayName(string first, string last, string expected)
    {
        PersonName.Compose(first, last).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a chosen order applies to every script, and spacing still follows the script.
    /// </summary>
    /// <param name="first">The given name.</param>
    /// <param name="last">The family name.</param>
    /// <param name="order">The order.</param>
    /// <param name="expected">The display name.</param>
    [Theory]
    [InlineData("Alice", "Walker", NameOrder.FamilyFirst, "Walker Alice")]
    [InlineData("Alice", "Walker", NameOrder.GivenFirst, "Alice Walker")]
    [InlineData("三", "张", NameOrder.FamilyFirst, "张三")]
    [InlineData("三", "张", NameOrder.GivenFirst, "三张")]
    [InlineData("", "张", NameOrder.GivenFirst, "张")]
    [InlineData("Alice", "", NameOrder.FamilyFirst, "Alice")]
    public void Compose_ChosenOrder_ReturnsDisplayName(string first, string last, NameOrder order, string expected)
    {
        PersonName.Compose(first, last, order).Should().Be(expected);
        Contact.Create(first, last, ["a@x.com"]).FormatName(order).Should().Be(expected);
    }

    /// <summary>
    /// Verifies how a name given as one text is split.
    /// </summary>
    /// <param name="name">The display name.</param>
    /// <param name="first">The expected given name.</param>
    /// <param name="last">The expected family name.</param>
    [Theory]
    [InlineData("John Doe", "John", "Doe")]
    [InlineData("Mary Jane Watson", "Mary Jane", "Watson")]
    [InlineData("Doe, John", "John", "Doe")]
    [InlineData("张三", "三", "张")]
    [InlineData("欧阳小明", "小明", "欧阳")]
    [InlineData("张 小明", "小明", "张")]
    [InlineData("深圳招商银行", "深圳招商银行", "")]
    [InlineData("John", "John", "")]
    [InlineData(null, "", "")]
    public void Split_DisplayName_ReturnsParts(string? name, string first, string last)
    {
        PersonName.Split(name).Should().Be((first, last));
    }

    /// <summary>
    /// Verifies which texts count as addresses.
    /// </summary>
    /// <param name="address">The text.</param>
    /// <param name="expected">Whether it is an address.</param>
    [Theory]
    [InlineData("a@x.com", true)]
    [InlineData("用户@例子.中国", true)]
    [InlineData("a@localhost", true)]
    [InlineData("@x.com", false)]
    [InlineData("a@", false)]
    [InlineData("a b@x.com", false)]
    [InlineData("Name <a@x.com>", false)]
    [InlineData("a@x.com,b@x.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidEmail_Text_ReturnsExpected(string? address, bool expected)
    {
        Contact.IsValidEmail(address).Should().Be(expected);
    }
}
