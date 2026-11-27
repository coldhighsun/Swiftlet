using Avalonia;
using AwesomeAssertions;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.Views;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the new mail notification: its content, its position and when it appears.
/// </summary>
public class NewMailTests
{
    /// <summary>
    /// Creates an arrival of one or more messages, each given as subject and age rank (higher is newer).
    /// </summary>
    /// <param name="account">The account address.</param>
    /// <param name="messages">The messages.</param>
    /// <returns>The event data.</returns>
    private static NewMailEventArgs Arrival(string account, params (string Subject, int Minute)[] messages)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new NewMailEventArgs(
            Guid.NewGuid(),
            account,
            [.. messages.Select(m => new NewMailSummary("Alice", "alice@example.com", m.Subject, start.AddMinutes(m.Minute)))]);
    }

    /// <summary>
    /// Verifies a single message shows its sender, subject and account.
    /// </summary>
    [Fact]
    public void ViewModel_OneMessage_ShowsSenderSubjectAndAccount()
    {
        using var viewModel = new NewMailViewModel(Arrival("me@example.com", ("Hello", 1)), TestLocalizer.English());

        viewModel.Title.Should().Be("New message");
        viewModel.Sender.Should().Be("Alice");
        viewModel.Subject.Should().Be("Hello");
        viewModel.Footer.Should().Be("me@example.com");
    }

    /// <summary>
    /// Verifies several messages are counted and the newest one is shown.
    /// </summary>
    [Fact]
    public void ViewModel_SeveralMessages_ShowsNewestAndCount()
    {
        using var viewModel = new NewMailViewModel(
            Arrival("me@example.com", ("newest", 3), ("old", 1), ("middle", 2)),
            TestLocalizer.English());

        viewModel.Title.Should().Be("3 new messages");
        viewModel.Subject.Should().Be("newest");
        viewModel.Footer.Should().Be("me@example.com · 2 more");
    }

    /// <summary>
    /// Verifies a missing sender name and subject have replacements.
    /// </summary>
    [Fact]
    public void ViewModel_BlankNameAndSubject_UsesAddressAndPlaceholder()
    {
        var arrival = new NewMailEventArgs(
            Guid.NewGuid(),
            "me@example.com",
            [new NewMailSummary(" ", "bob@example.com", string.Empty, DateTimeOffset.UnixEpoch)]);

        using var viewModel = new NewMailViewModel(arrival, TestLocalizer.English());

        viewModel.Sender.Should().Be("bob@example.com");
        viewModel.Subject.Should().Be("(No subject)");
    }

    /// <summary>
    /// Verifies added mail raises the count, and only newer mail replaces the shown message.
    /// </summary>
    [Fact]
    public void ViewModel_Add_CountsAllAndKeepsTheNewest()
    {
        using var viewModel = new NewMailViewModel(Arrival("a@example.com", ("current", 5)), TestLocalizer.English());

        viewModel.Add(Arrival("b@example.com", ("earlier", 1)));
        viewModel.Subject.Should().Be("current");
        viewModel.Footer.Should().Be("a@example.com · 1 more");

        viewModel.Add(Arrival("b@example.com", ("later", 9)));

        viewModel.Title.Should().Be("3 new messages");
        viewModel.Subject.Should().Be("later");
        viewModel.Footer.Should().Be("b@example.com · 2 more");
    }

    /// <summary>
    /// Verifies the texts follow a language change until the view model is disposed.
    /// </summary>
    [Fact]
    public void ViewModel_LanguageChange_RefreshesTextsUntilDisposed()
    {
        var localizer = TestLocalizer.English();
        var viewModel = new NewMailViewModel(Arrival("me@example.com", ("Hello", 1)), localizer);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        localizer.SetPreference(LocalizationManager.SimplifiedChinese);

        changes.Should().Contain(nameof(NewMailViewModel.Title));
        viewModel.Title.Should().Be("新邮件");

        viewModel.Dispose();
        changes.Clear();
        localizer.SetPreference(LocalizationManager.English);
        changes.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the pop-up sits inside the bottom right corner, also on a scaled screen that does not start at zero.
    /// </summary>
    /// <param name="scaling">The screen scaling.</param>
    /// <param name="x">The expected left edge.</param>
    /// <param name="y">The expected top edge.</param>
    [Theory]
    [InlineData(1.0, 1624, 944)]
    [InlineData(1.5, 1436, 886)]
    public void CornerPosition_ScaledScreen_KeepsMarginToTheCorner(double scaling, int x, int y)
    {
        var position = NewMailWindow.CornerPosition(new PixelRect(100, 0, 1900, 1060), new Size(360, 100), scaling);

        position.Should().Be(new PixelPoint(x, y));
    }

    /// <summary>
    /// Verifies arriving mail shows the pop-up.
    /// </summary>
    [Fact]
    public void Notifier_NewMail_ShowsPopup()
    {
        var scenario = new Scenario();

        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("Hello", 1)));

        scenario.Presenter.Shown.Should().ContainSingle().Which.Subject.Should().Be("Hello");
    }

    /// <summary>
    /// Verifies nothing is shown while the setting is off.
    /// </summary>
    [Fact]
    public void Notifier_SettingOff_ShowsNothing()
    {
        var scenario = new Scenario();
        scenario.Settings.ShowNewMailNotifications = false;

        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("Hello", 1)));

        scenario.Presenter.Shown.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies mail arriving while the pop-up is shown is added to it.
    /// </summary>
    [Fact]
    public void Notifier_SecondArrival_ExtendsTheShownPopup()
    {
        var scenario = new Scenario();

        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("first", 1)));
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("second", 2)));

        scenario.Presenter.Shown.Should().HaveCount(2);
        scenario.Presenter.Shown[1].Should().BeSameAs(scenario.Presenter.Shown[0]);
        scenario.Presenter.Shown[1].Title.Should().Be("2 new messages");
    }

    /// <summary>
    /// Verifies a closed pop-up is removed and the next arrival counts from the beginning.
    /// </summary>
    [Fact]
    public void Notifier_CloseThenArrival_StartsOver()
    {
        var scenario = new Scenario();
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("first", 1)));

        scenario.Presenter.Shown[0].CloseCommand.Execute(null);
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("second", 2)));

        scenario.Presenter.Closed.Should().Be(1);
        scenario.Opened.Should().Be(0);
        scenario.Presenter.Shown[1].Should().NotBeSameAs(scenario.Presenter.Shown[0]);
        scenario.Presenter.Shown[1].Title.Should().Be("New message");
    }

    /// <summary>
    /// Verifies clicking the pop-up removes it and shows the mail.
    /// </summary>
    [Fact]
    public void Notifier_Open_ClosesPopupAndOpensMail()
    {
        var scenario = new Scenario();
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("first", 1)));

        scenario.Presenter.Shown[0].OpenCommand.Execute(null);

        scenario.Presenter.Closed.Should().Be(1);
        scenario.Opened.Should().Be(1);
    }

    /// <summary>
    /// Verifies a disposed notifier removes the pop-up and stops listening.
    /// </summary>
    [Fact]
    public void Notifier_Dispose_ClosesPopupAndUnsubscribes()
    {
        var scenario = new Scenario();
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("first", 1)));

        scenario.Notifier.Dispose();

        scenario.Presenter.Closed.Should().Be(1);
        scenario.Events.HasNewMailSubscribers.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a pop-up that cannot be shown does not disturb the caller.
    /// </summary>
    [Fact]
    public void Notifier_PresenterFails_DoesNotThrow()
    {
        var scenario = new Scenario();
        scenario.Presenter.Failure = new InvalidOperationException("no screen");

        var raise = () => scenario.Events.RaiseNewMail(Arrival("me@example.com", ("first", 1)));

        raise.Should().NotThrow();
        scenario.Presenter.Failure = null;
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("second", 2)));
        scenario.Presenter.Shown.Should().ContainSingle().Which.Title.Should().Be("2 new messages");
    }

    /// <summary>
    /// A notifier wired to fakes.
    /// </summary>
    private sealed class Scenario
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Scenario"/> class.
        /// </summary>
        public Scenario()
        {
            Notifier = new NewMailNotifier(Events, Settings, TestLocalizer.English(), UiThread, Presenter, () => Opened++);
        }

        /// <summary>
        /// Gets the notification source.
        /// </summary>
        public FakeSyncEvents Events { get; } = new();

        /// <summary>
        /// Gets the settings.
        /// </summary>
        public FakeAppSettings Settings { get; } = new();

        /// <summary>
        /// Gets the UI thread marshaller.
        /// </summary>
        public FakeUiThread UiThread { get; } = new();

        /// <summary>
        /// Gets the pop-up.
        /// </summary>
        public FakeNewMailPresenter Presenter { get; } = new();

        /// <summary>
        /// Gets the notifier under test.
        /// </summary>
        public NewMailNotifier Notifier { get; }

        /// <summary>
        /// Gets how often the mail was opened.
        /// </summary>
        public int Opened { get; private set; }
    }
}
