using System.Text;
using AwesomeAssertions;
using Swiftlet.Application.Contacts;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests the recipient list parser and the contact file formats.
/// </summary>
public sealed class ContactFormatTests
{
    /// <summary>
    /// Verifies lists accept commas, semicolons (including full-width) and line breaks.
    /// </summary>
    [Fact]
    public void Split_MixedSeparators_ReturnsTrimmedEntries()
    {
        var result = RecipientList.Split(" a@x.com; b@x.com，c@x.com\nd@x.com ,, ");

        result.Should().Equal("a@x.com", "b@x.com", "c@x.com", "d@x.com");
        RecipientList.Split(null).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies separators inside a quoted name or angle brackets do not split an entry.
    /// </summary>
    [Fact]
    public void Split_QuotedName_KeepsEntryTogether()
    {
        var result = RecipientList.Split("\"Doe, \\\"JD\\\"; John\" <j@x.com>, O\"Brien <o@x.com>, b@x.com");

        result.Should().Equal("\"Doe, \\\"JD\\\"; John\" <j@x.com>", "O\"Brien <o@x.com>", "b@x.com");
    }

    /// <summary>
    /// Verifies formatting and parsing are inverse operations.
    /// </summary>
    /// <param name="name">The display name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("张三")]
    [InlineData("Doe, John")]
    [InlineData("A \"B\" \\ C")]
    [InlineData("张三，经理")]
    public void Format_ThenParse_ReturnsSameRecipient(string name)
    {
        var entry = RecipientList.Format(name, "j@x.com");

        RecipientList.Split($"{entry}, other@x.com").Should().HaveCount(2);
        RecipientList.Parse(entry).Should().Be(new Recipient(name, "j@x.com"));
    }

    /// <summary>
    /// Verifies the start of the last entry ignores separators inside quotes.
    /// </summary>
    [Fact]
    public void LastEntryStart_UnfinishedEntry_ReturnsPositionAfterLastSeparator()
    {
        RecipientList.LastEntryStart("a@x.com, bo").Should().Be(8);
        RecipientList.LastEntryStart("\"Doe, Jo").Should().Be(0);
    }

    /// <summary>
    /// Verifies written cards are read back unchanged, including long Chinese text that has to be folded.
    /// </summary>
    [Fact]
    public void VCard_WriteThenRead_KeepsDetails()
    {
        var contact = new ContactDetails(
            "三",
            "张",
            ["a@x.com", "b@x.com"],
            ["+86 138"],
            "Acme; Inc, Ltd",
            string.Concat("first line\n", new string('长', 100)));

        var text = VCardFormat.Write([contact]);

        text.Split("\r\n").Should().OnlyContain(l => Encoding.UTF8.GetByteCount(l) <= 75);
        VCardFormat.Read(text).Should().ContainSingle().Which.Should().BeEquivalentTo(contact);
    }

    /// <summary>
    /// Verifies a version 2.1 card with quoted-printable values and a structured name is read.
    /// </summary>
    [Fact]
    public void VCard_QuotedPrintableVersion21_DecodesValues()
    {
        const string text = """
            BEGIN:VCARD
            VERSION:2.1
            N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=E5=BC=A0;=E4=B8=
            =89;;;
            item1.EMAIL;TYPE=INTERNET:a@x.com
            TEL;CELL:tel:123
            END:VCARD
            """;

        var card = VCardFormat.Read(text).Should().ContainSingle().Subject;

        (card.FirstName, card.LastName).Should().Be(("三", "张"));
        card.Emails.Should().Equal("a@x.com");
        card.Phones.Should().Equal("123");
    }

    /// <summary>
    /// Verifies a card or row with only a full name has it split.
    /// </summary>
    [Fact]
    public void Read_FullNameOnly_SplitsName()
    {
        var card = VCardFormat.Read("BEGIN:VCARD\nFN:John Doe\nEMAIL:a@x.com\nEND:VCARD").Single();
        var row = CsvContactFormat.Read("Name,Email\n张三,a@x.com\n").Single();

        (card.FirstName, card.LastName).Should().Be(("John", "Doe"));
        (row.FirstName, row.LastName).Should().Be(("三", "张"));
    }

    /// <summary>
    /// Verifies a text without cards is rejected.
    /// </summary>
    [Fact]
    public void VCard_NoCard_Throws()
    {
        var act = () => VCardFormat.Read("name,email");

        act.Should().Throw<FormatException>();
    }

    /// <summary>
    /// Verifies the columns of a Google export are recognized.
    /// </summary>
    [Fact]
    public void Csv_GoogleColumns_ReadsValues()
    {
        const string text = "First Name,Last Name,Phonetic First Name,E-mail 1 - Label,E-mail 1 - Value,Phone 1 - Value,Organization Name,Notes\r\n"
            + "John,Doe,jon,Work,a@x.com ::: b@x.com,123,Acme,\"two\nlines, \"\"quoted\"\"\"\r\n\r\n";

        var row = CsvContactFormat.Read(text).Should().ContainSingle().Subject;

        (row.FirstName, row.LastName).Should().Be(("John", "Doe"));
        row.Emails.Should().Equal("a@x.com", "b@x.com");
        row.Phones.Should().Equal("123");
        row.Company.Should().Be("Acme");
        row.Note.Should().Be("two\nlines, \"quoted\"");
    }

    /// <summary>
    /// Verifies Chinese columns are recognized and Chinese names put the family name first.
    /// </summary>
    [Fact]
    public void Csv_ChineseColumns_ReadsValues()
    {
        const string text = "名;姓;电子邮件地址;电子邮件显示名称;移动电话;公司\n三;张;a@x.com;张三 (a@x.com);138;公司甲\n";

        var row = CsvContactFormat.Read(text).Should().ContainSingle().Subject;

        (row.FirstName, row.LastName).Should().Be(("三", "张"));
        row.Emails.Should().Equal("a@x.com");
        row.Phones.Should().Equal("138");
        row.Company.Should().Be("公司甲");
    }

    /// <summary>
    /// Verifies a file without an email column is rejected.
    /// </summary>
    [Fact]
    public void Csv_NoEmailColumn_Throws()
    {
        var act = () => CsvContactFormat.Read("Name,Phone\nA,1\n");

        act.Should().Throw<FormatException>();
    }

    /// <summary>
    /// Verifies formula-like values are guarded on export and restored on import.
    /// </summary>
    [Fact]
    public void Csv_WriteThenRead_GuardsFormulasAndKeepsDetails()
    {
        var contact = new ContactDetails("=SUM(A1)", "Last", ["a@x.com", "b@x.com"], ["+86 138"], "@Acme", "a, b");

        var text = CsvContactFormat.Write([contact]);

        text.Should().Contain("\"'=SUM(A1)\"").And.Contain("\"'@Acme\"");
        CsvContactFormat.Read(text).Should().ContainSingle().Which.Should().BeEquivalentTo(contact);
    }
}
