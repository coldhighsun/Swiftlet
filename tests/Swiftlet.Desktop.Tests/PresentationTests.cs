using AwesomeAssertions;
using Avalonia.Media;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the values the views show: list times, avatars, icons and label tints.
/// </summary>
public class PresentationTests
{
    /// <summary>
    /// Verifies the avatar shows the whole first character of the name, falling back to the address.
    /// </summary>
    /// <param name="name">The sender name.</param>
    /// <param name="address">The sender address.</param>
    /// <param name="expected">The expected initial.</param>
    [Theory]
    [InlineData("alice", "a@x.com", "A")]
    [InlineData("  张伟", "z@x.com", "张")]
    [InlineData("😀 Bob", "b@x.com", "😀")]
    [InlineData("   ", "carol@x.com", "C")]
    [InlineData(null, "dan@x.com", "D")]
    [InlineData("", null, "?")]
    [InlineData(null, "  ", "?")]
    public void Initial_VariousNames_ReturnsWholeFirstCharacter(string? name, string? address, string expected)
    {
        Avatar.Initial(name, address).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a sender keeps one avatar color however the address is written, and senders differ.
    /// </summary>
    [Fact]
    public void Color_SameAddressDifferentCase_IsSame()
    {
        Avatar.Color("Alice@Example.com ").Should().Be(Avatar.Color("alice@example.com"));
        Enumerable.Range(0, 40).Select(i => Avatar.Color($"user{i}@example.com")).Distinct().Should().HaveCountGreaterThan(3);
        Avatar.Color(null).Should().Be(Avatar.Color(string.Empty));
    }

    /// <summary>
    /// Verifies the white initial stays readable on every avatar color.
    /// </summary>
    [Fact]
    public void Color_Palette_HasContrastWithWhite()
    {
        Avatar.Palette.Should().NotBeEmpty().And.OnlyContain(c => ThemeTests.Contrast(Color.Parse(c), Colors.White) >= 4.5);
        Swiftlet.Domain.Accounts.AccountColors.Palette.Should()
            .OnlyContain(c => ThemeTests.Contrast(Color.Parse(c), Colors.White) >= 4.5);
    }

    /// <summary>
    /// Verifies the list shows the time for today, a word for yesterday and a date for older mail.
    /// </summary>
    /// <param name="sentAt">When the message was sent.</param>
    /// <param name="expected">The expected text.</param>
    [Theory]
    [InlineData("2026-03-10T08:05:00Z", "08:05")]
    [InlineData("2026-03-09T23:59:00Z", "昨天")]
    [InlineData("2026-03-08T12:00:00Z", "03-08")]
    [InlineData("2026-01-02T12:00:00Z", "01-02")]
    [InlineData("2025-12-31T12:00:00Z", "2025-12-31")]
    public void Format_RelativeToNow_PicksTheShortestClearForm(string sentAt, string expected)
    {
        var clock = ClockAt("2026-03-10T09:00:00Z");

        var text = MessageTimeFormatter.Format(Parse(sentAt), clock, TestLocalizer.Chinese());

        text.Should().Be(expected);
    }

    /// <summary>
    /// Verifies the last day of the old year is still called yesterday on New Year's Day.
    /// </summary>
    [Fact]
    public void Format_NewYearsDay_PreviousDayIsYesterday()
    {
        var clock = ClockAt("2026-01-01T10:00:00Z");

        MessageTimeFormatter.Format(Parse("2025-12-31T22:00:00Z"), clock, TestLocalizer.English()).Should().Be("Yesterday");
    }

    /// <summary>
    /// Verifies an item left open across midnight shows the new form after the next refresh.
    /// </summary>
    [Fact]
    public void Update_AfterMidnight_RefreshesListTime()
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1);
        var clock = ClockAt("2026-01-01T23:00:00Z");
        var item = new MessageItemViewModel(message, _ => Task.CompletedTask, _ => Task.CompletedTask, TestLocalizer.Chinese(), timeProvider: clock);
        item.ListTimeText.Should().Be("00:01");
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        clock.Advance(TimeSpan.FromHours(2));
        item.Update(message);

        changed.Should().Contain(nameof(MessageItemViewModel.ListTimeText));
        item.ListTimeText.Should().Be("昨天");
    }

    /// <summary>
    /// Verifies the list time follows a language switch.
    /// </summary>
    [Fact]
    public void RefreshLanguage_Yesterday_IsRelabelled()
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1);
        var localizer = TestLocalizer.Chinese();
        var item = new MessageItemViewModel(message, _ => Task.CompletedTask, _ => Task.CompletedTask, localizer, timeProvider: ClockAt("2026-01-02T10:00:00Z"));
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        localizer.SetPreference("en");
        item.RefreshLanguage();

        changed.Should().Contain(nameof(MessageItemViewModel.ListTimeText));
        item.ListTimeText.Should().Be("Yesterday");
    }

    /// <summary>
    /// Verifies unread state, sender initial and attachment texts of a list item.
    /// </summary>
    [Fact]
    public void MessageItem_Attachment_ExposesNameSizeAndUnreadState()
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1);
        message.AddAttachment("report.pdf", "application/pdf", 1_250_000);
        var item = new MessageItemViewModel(message, _ => Task.CompletedTask, _ => Task.CompletedTask, TestLocalizer.English());
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsUnread.Should().BeTrue();
        item.SenderInitial.Should().Be("A");
        var attachment = item.Attachments.Single();
        attachment.FileName.Should().Be("report.pdf");
        attachment.SizeText.Should().Be(FileSizeFormatter.Format(1_250_000));
        attachment.Label.Should().Be($"report.pdf ({attachment.SizeText})");

        item.IsRead = true;

        item.IsUnread.Should().BeFalse();
        changed.Should().Contain(nameof(MessageItemViewModel.IsUnread));
    }

    /// <summary>
    /// Verifies every folder role has its own icon resource.
    /// </summary>
    /// <param name="role">The folder role.</param>
    /// <param name="expected">The expected resource key.</param>
    [Theory]
    [InlineData(FolderRole.Inbox, "Icon.Inbox")]
    [InlineData(FolderRole.Sent, "Icon.Sent")]
    [InlineData(FolderRole.Drafts, "Icon.Drafts")]
    [InlineData(FolderRole.Trash, "Icon.Trash")]
    [InlineData(FolderRole.Junk, "Icon.Junk")]
    [InlineData(FolderRole.Archive, "Icon.Archive")]
    [InlineData(FolderRole.AllMail, "Icon.AllMail")]
    [InlineData(FolderRole.Other, "Icon.Folder")]
    public void IconKey_EachRole_MatchesIconResource(FolderRole role, string expected)
    {
        var account = TestData.Account();
        var node = new FolderNodeViewModel(TestData.Folder(account, "x", role), account, TestLocalizer.English());

        node.IconKey.Should().Be(expected);
    }

    /// <summary>
    /// Verifies the account avatar and the light label tint.
    /// </summary>
    [Fact]
    public async Task AccountAvatarAndLabelTint_UseTheExpectedColors()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                var node = new AccountNodeViewModel(TestData.Account("zed@example.com"));
                node.Initial.Should().Be("Z", "the tree shows the address when the user gave the account no name");
                ((ISolidColorBrush)node.AvatarBrush).Color.Should().Be(Color.Parse(Avatar.Color("zed@example.com")));

                var chip = new LabelChipViewModel("Work", "#E53935");
                ((ISolidColorBrush)chip.TintBrush).Color.Should().Be(Color.Parse("#26E53935"));
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the tree shows a name the user typed, and the address instead of one that was filled in.
    /// </summary>
    /// <param name="custom">Whether the user typed the name.</param>
    /// <param name="expected">The text shown in the tree.</param>
    /// <param name="initial">The avatar initial.</param>
    [Theory]
    [InlineData(true, "Work", "W")]
    [InlineData(false, "zed@example.com", "Z")]
    public void AccountNode_DisplayName_PrefersTheTypedName(bool custom, string expected, string initial)
    {
        var account = MailAccount.Create(
            "Work",
            "zed@example.com",
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow,
            hasCustomDisplayName: custom);

        var node = new AccountNodeViewModel(account);

        node.DisplayName.Should().Be(expected);
        node.Initial.Should().Be(initial);
    }

    /// <summary>
    /// Creates a clock showing a given instant.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The clock.</returns>
    private static FakeClock ClockAt(string instant)
    {
        var clock = new FakeClock();
        clock.Advance(Parse(instant) - clock.GetUtcNow());
        return clock;
    }

    /// <summary>
    /// Parses an ISO instant.
    /// </summary>
    /// <param name="instant">The text.</param>
    /// <returns>The instant.</returns>
    private static DateTimeOffset Parse(string instant)
    {
        return DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);
    }
}
