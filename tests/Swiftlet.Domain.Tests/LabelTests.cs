using AwesomeAssertions;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Domain.Tests;

/// <summary>
/// Tests for <see cref="Label"/> and the label operations of <see cref="MailMessage"/>.
/// </summary>
public class LabelTests
{
    /// <summary>
    /// Creates a message for label tests.
    /// </summary>
    /// <returns>The message.</returns>
    private static MailMessage CreateMessage()
    {
        return MailMessage.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, "s", "Alice", "alice@example.com", "me@example.com", DateTimeOffset.UtcNow, "body");
    }

    /// <summary>
    /// Verifies invalid colors are rejected.
    /// </summary>
    /// <param name="color">The invalid color.</param>
    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("123456")]
    [InlineData("")]
    [InlineData("#112233\n")]
    public void Create_InvalidColor_Throws(string color)
    {
        var act = () => Label.Create("Work", color);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies blank names are rejected.
    /// </summary>
    /// <param name="name">The blank name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_Throws(string name)
    {
        var act = () => Label.Create(name, "#112233");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies names longer than 50 characters are rejected.
    /// </summary>
    [Fact]
    public void Create_TooLongName_Throws()
    {
        var act = () => Label.Create(new string('a', 51), "#112233");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies a name of exactly the maximum length is accepted.
    /// </summary>
    [Fact]
    public void Create_NameOfMaxLength_IsAccepted()
    {
        var label = Label.Create(new string('a', Label.MaxNameLength), "#112233");

        label.Name.Should().HaveLength(50);
    }

    /// <summary>
    /// Verifies a null name is rejected.
    /// </summary>
    [Fact]
    public void Create_NullName_Throws()
    {
        var act = () => Label.Create(null!, "#112233");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies attaching a null label is rejected.
    /// </summary>
    [Fact]
    public void AddLabel_Null_Throws()
    {
        var message = CreateMessage();

        var act = () => message.AddLabel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies the name is trimmed.
    /// </summary>
    [Fact]
    public void Create_NameWithWhitespace_Trims()
    {
        var label = Label.Create("  Work  ", "#aBc123");

        label.Name.Should().Be("Work");
        label.Color.Should().Be("#aBc123");
    }

    /// <summary>
    /// Verifies renaming to a blank name is rejected.
    /// </summary>
    [Fact]
    public void Rename_Blank_Throws()
    {
        var label = Label.Create("Work", "#112233");

        var act = () => label.Rename("  ");

        act.Should().Throw<ArgumentException>();
        label.Name.Should().Be("Work");
    }

    /// <summary>
    /// Verifies renaming trims and applies the name.
    /// </summary>
    [Fact]
    public void Rename_Valid_UpdatesName()
    {
        var label = Label.Create("Work", "#112233");

        label.Rename(" Home ");

        label.Name.Should().Be("Home");
    }

    /// <summary>
    /// Verifies an invalid color is rejected when changing color.
    /// </summary>
    [Fact]
    public void SetColor_Invalid_Throws()
    {
        var label = Label.Create("Work", "#112233");

        var act = () => label.SetColor("blue");

        act.Should().Throw<ArgumentException>();
        label.Color.Should().Be("#112233");
    }

    /// <summary>
    /// Verifies a valid color is applied.
    /// </summary>
    [Fact]
    public void SetColor_Valid_UpdatesColor()
    {
        var label = Label.Create("Work", "#112233");

        label.SetColor("#FFFFFF");

        label.Color.Should().Be("#FFFFFF");
    }

    /// <summary>
    /// Verifies adding the same label twice keeps one entry.
    /// </summary>
    [Fact]
    public void AddLabel_Twice_AddsOnce()
    {
        var message = CreateMessage();
        var label = Label.Create("Work", "#112233");

        message.AddLabel(label);
        message.AddLabel(label);

        message.Labels.Should().ContainSingle().Which.Should().BeSameAs(label);
    }

    /// <summary>
    /// Verifies removing a label that is not attached does nothing.
    /// </summary>
    [Fact]
    public void RemoveLabel_Unknown_IsNoOp()
    {
        var message = CreateMessage();
        message.AddLabel(Label.Create("Work", "#112233"));

        message.RemoveLabel(Guid.NewGuid());

        message.Labels.Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies removing an attached label removes it.
    /// </summary>
    [Fact]
    public void RemoveLabel_Existing_Removes()
    {
        var message = CreateMessage();
        var label = Label.Create("Work", "#112233");
        message.AddLabel(label);

        message.RemoveLabel(label.Id);

        message.Labels.Should().BeEmpty();
    }
}
