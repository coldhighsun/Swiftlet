using Avalonia;
using AwesomeAssertions;
using Swiftlet.Application.Contacts;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
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
            [.. messages.Select(m => new NewMailSummary(
                "Alice", "alice@example.com", m.Subject, start.AddMinutes(m.Minute), Guid.NewGuid(), Guid.NewGuid(), $"Text of {m.Subject}"))]);
    }

    /// <summary>
    /// Creates a dispatcher answering the requests of the pop-up.
    /// </summary>
    /// <param name="result">How every action ends.</param>
    /// <param name="availability">Which actions are possible; all of them when omitted.</param>
    /// <returns>The dispatcher.</returns>
    private static FakeDispatcher Dispatcher(
        QuickMailActionResult result = QuickMailActionResult.Done,
        QuickActionAvailability? availability = null)
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<QuickMailActionCommand>(_ => result);
        dispatcher.On<GetQuickActionsQuery>(_ => availability ?? new QuickActionAvailability(true, true));
        return dispatcher;
    }

    /// <summary>
    /// Creates a view model and waits until its buttons are up to date.
    /// </summary>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="arrival">The first arrival.</param>
    /// <param name="changed">Told about changes.</param>
    /// <returns>The view model.</returns>
    private static async Task<NewMailViewModel> CreateAsync(FakeDispatcher dispatcher, NewMailEventArgs arrival, Action? changed = null)
    {
        var viewModel = new NewMailViewModel(arrival, dispatcher, TestLocalizer.English(), changed ?? (() => { }));
        await viewModel.AvailabilityLoad;
        return viewModel;
    }

    /// <summary>
    /// Verifies a single message shows its sender, subject, preview and account.
    /// </summary>
    [Fact]
    public async Task ViewModel_OneMessage_ShowsItsTexts()
    {
        using var viewModel = await CreateAsync(Dispatcher(), Arrival("me@example.com", ("Hello", 1)));

        viewModel.Title.Should().Be("New message");
        viewModel.Sender.Should().Be("Alice");
        viewModel.Subject.Should().Be("Hello");
        viewModel.Preview.Should().Be("Text of Hello");
        viewModel.AccountAddress.Should().Be("me@example.com");
        viewModel.HasSeveral.Should().BeFalse();
        viewModel.PreviousCommand.CanExecute(null).Should().BeFalse();
        viewModel.NextCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies several messages are counted and shown newest first.
    /// </summary>
    [Fact]
    public async Task ViewModel_SeveralMessages_ShowsNewestFirst()
    {
        using var viewModel = await CreateAsync(
            Dispatcher(),
            Arrival("me@example.com", ("old", 1), ("newest", 3), ("middle", 2)));

        viewModel.Title.Should().Be("3 new messages");
        viewModel.Subject.Should().Be("newest");
        viewModel.Position.Should().Be("1 of 3");
        viewModel.NextCommand.Execute(null);
        viewModel.Subject.Should().Be("middle");
        viewModel.Position.Should().Be("2 of 3");
    }

    /// <summary>
    /// Verifies a missing sender name and subject have replacements.
    /// </summary>
    [Fact]
    public async Task ViewModel_BlankNameAndSubject_UsesAddressAndPlaceholder()
    {
        var arrival = new NewMailEventArgs(
            Guid.NewGuid(),
            "me@example.com",
            [new NewMailSummary(" ", "bob@example.com", string.Empty, DateTimeOffset.UnixEpoch, Guid.NewGuid(), Guid.NewGuid(), string.Empty)]);

        using var viewModel = await CreateAsync(Dispatcher(), arrival);

        viewModel.Sender.Should().Be("bob@example.com");
        viewModel.Subject.Should().Be("(No subject)");
    }

    /// <summary>
    /// Verifies a sender and a receiving account in the address book show with their contact names, loading the address
    /// book if needed.
    /// </summary>
    [Fact]
    public async Task ViewModel_AddressesAreContacts_ShowsContactNamesAndAddresses()
    {
        var dispatcher = Dispatcher();
        dispatcher.On<GetContactNamesQuery>(_ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["alice@example.com"] = "Alice Smith",
            ["me@example.com"] = "My Work Mail",
        });

        using var viewModel = new NewMailViewModel(
            Arrival("me@example.com", ("Hello", 1)),
            dispatcher,
            TestLocalizer.English(),
            () => { },
            contacts: new ContactDirectory(dispatcher));
        await viewModel.ContactsLoad;

        viewModel.Sender.Should().Be("Alice Smith <alice@example.com>");
        viewModel.AccountAddress.Should().Be("My Work Mail <me@example.com>");
    }

    /// <summary>
    /// Verifies later mail joins the end of the queue, so the shown message does not change under the pointer.
    /// </summary>
    [Fact]
    public async Task ViewModel_Add_KeepsTheShownMessage()
    {
        using var viewModel = await CreateAsync(Dispatcher(), Arrival("a@example.com", ("current", 5)));
        var later = Arrival("b@example.com", ("later", 9));

        viewModel.Add(later);
        viewModel.Add(later);

        viewModel.Title.Should().Be("2 new messages");
        viewModel.Subject.Should().Be("current");
        viewModel.Position.Should().Be("1 of 2");
        viewModel.NextCommand.CanExecute(null).Should().BeTrue();
        viewModel.NextCommand.Execute(null);
        viewModel.Subject.Should().Be("later");
        viewModel.AccountAddress.Should().Be("b@example.com");
    }

    /// <summary>
    /// Verifies every action is sent for the shown message, reports the change and moves on.
    /// </summary>
    /// <param name="action">The action.</param>
    [Theory]
    [InlineData(QuickMailAction.MarkRead)]
    [InlineData(QuickMailAction.Star)]
    [InlineData(QuickMailAction.Archive)]
    [InlineData(QuickMailAction.Delete)]
    public async Task Action_Done_SendsItForTheShownMessageAndShowsTheNext(QuickMailAction action)
    {
        var dispatcher = Dispatcher();
        var arrival = Arrival("me@example.com", ("first", 2), ("second", 1));
        var changes = 0;
        using var viewModel = await CreateAsync(dispatcher, arrival, () => changes++);
        var command = action switch
        {
            QuickMailAction.MarkRead => viewModel.MarkReadCommand,
            QuickMailAction.Star => viewModel.StarCommand,
            QuickMailAction.Archive => viewModel.ArchiveCommand,
            _ => viewModel.DeleteCommand,
        };

        await command.ExecuteAsync(null);

        var shown = arrival.Messages[0];
        dispatcher.Sent<QuickMailActionCommand>().Should().Equal(
            new QuickMailActionCommand(arrival.AccountId, shown.MessageId, shown.FolderId, action));
        changes.Should().Be(1);
        viewModel.Subject.Should().Be("second");
        viewModel.Title.Should().Be("New message");
        viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies handling the last message of the queue goes back to the one before it, which stayed unread.
    /// </summary>
    [Fact]
    public async Task Action_AtTheEnd_ShowsThePreviousMessage()
    {
        using var viewModel = await CreateAsync(Dispatcher(), Arrival("me@example.com", ("skipped", 2), ("last", 1)));
        viewModel.NextCommand.Execute(null);
        await viewModel.AvailabilityLoad;

        await viewModel.MarkReadCommand.ExecuteAsync(null);

        viewModel.Subject.Should().Be("skipped");
        viewModel.HasSeveral.Should().BeFalse();
    }

    /// <summary>
    /// Verifies handling the only message asks to close the pop-up.
    /// </summary>
    [Fact]
    public async Task Action_OnTheOnlyMessage_RequestsClose()
    {
        using var viewModel = await CreateAsync(Dispatcher(), Arrival("me@example.com", ("only", 1)));
        var closes = 0;
        var interactions = 0;
        viewModel.CloseRequested += (_, _) => closes++;
        viewModel.Interacted += (_, _) => interactions++;

        await viewModel.DeleteCommand.ExecuteAsync(null);

        closes.Should().Be(1);
        interactions.Should().Be(0);
    }

    /// <summary>
    /// Verifies a message that was removed elsewhere is skipped without reporting a change.
    /// </summary>
    [Fact]
    public async Task Action_MessageGone_ShowsTheNextWithoutReportingAChange()
    {
        var changes = 0;
        using var viewModel = await CreateAsync(
            Dispatcher(QuickMailActionResult.Gone), Arrival("me@example.com", ("gone", 2), ("next", 1)), () => changes++);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        viewModel.Subject.Should().Be("next");
        changes.Should().Be(0);
    }

    /// <summary>
    /// Verifies an action the account cannot carry out keeps the message and looks the buttons up again.
    /// </summary>
    [Fact]
    public async Task Action_Unavailable_KeepsTheMessageAndRefreshesTheButtons()
    {
        var dispatcher = Dispatcher(QuickMailActionResult.Unavailable);
        using var viewModel = await CreateAsync(dispatcher, Arrival("me@example.com", ("stays", 1)));
        dispatcher.On<GetQuickActionsQuery>(_ => new QuickActionAvailability(true, false));

        await viewModel.ArchiveCommand.ExecuteAsync(null);
        await viewModel.AvailabilityLoad;

        viewModel.Subject.Should().Be("stays");
        viewModel.ArchiveCommand.CanExecute(null).Should().BeFalse();
        viewModel.HasError.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failed action keeps the message, says so, and the line disappears with the next message.
    /// </summary>
    [Fact]
    public async Task Action_Fails_KeepsTheMessageAndShowsAnError()
    {
        var dispatcher = Dispatcher();
        dispatcher.On<QuickMailActionCommand>(_ => throw new InvalidOperationException("disk full"));
        using var viewModel = await CreateAsync(dispatcher, Arrival("me@example.com", ("stays", 2), ("next", 1)));

        await viewModel.MarkReadCommand.ExecuteAsync(null);

        viewModel.Subject.Should().Be("stays");
        viewModel.Error.Should().Be("That did not work. Try again.");
        viewModel.IsBusy.Should().BeFalse();
        viewModel.NextCommand.Execute(null);
        viewModel.HasError.Should().BeFalse();
    }

    /// <summary>
    /// Verifies nothing else can start while an action runs.
    /// </summary>
    [Fact]
    public async Task Action_Running_DisablesActionsAndNavigation()
    {
        var dispatcher = Dispatcher();
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.OnAsync<QuickMailActionCommand>((_, _) => release.Task);
        using var viewModel = await CreateAsync(dispatcher, Arrival("me@example.com", ("first", 2), ("second", 1)));

        var running = viewModel.MarkReadCommand.ExecuteAsync(null);

        viewModel.IsBusy.Should().BeTrue();
        viewModel.StarCommand.CanExecute(null).Should().BeFalse();
        viewModel.ArchiveCommand.CanExecute(null).Should().BeFalse();
        viewModel.DeleteCommand.CanExecute(null).Should().BeFalse();
        viewModel.NextCommand.CanExecute(null).Should().BeFalse();
        release.SetResult(QuickMailActionResult.Done);
        await running;
        viewModel.StarCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// Verifies the buttons that move a message follow what the account can do and explain why they are disabled.
    /// </summary>
    [Fact]
    public async Task Availability_NoTrashAndNoArchive_DisablesBothWithAReason()
    {
        using var viewModel = await CreateAsync(
            Dispatcher(availability: new QuickActionAvailability(false, false)), Arrival("me@example.com", ("Hello", 1)));

        viewModel.DeleteCommand.CanExecute(null).Should().BeFalse();
        viewModel.ArchiveCommand.CanExecute(null).Should().BeFalse();
        viewModel.MarkReadCommand.CanExecute(null).Should().BeTrue();
        viewModel.DeleteHint.Should().Contain("Trash");
        viewModel.ArchiveHint.Should().Contain("archive folder");
    }

    /// <summary>
    /// Verifies a late answer about an earlier message does not decide the buttons of the shown one.
    /// </summary>
    [Fact]
    public async Task Availability_LateAnswer_IsDropped()
    {
        var dispatcher = Dispatcher();
        var late = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrival = Arrival("me@example.com", ("first", 2), ("second", 1));
        var slowFolder = arrival.Messages[0].FolderId;
        dispatcher.OnAsync<GetQuickActionsQuery>((query, _) => query.FolderId == slowFolder
            ? late.Task
            : Task.FromResult<object?>(new QuickActionAvailability(false, false)));
        using var viewModel = new NewMailViewModel(arrival, dispatcher, TestLocalizer.English(), () => { });
        var first = viewModel.AvailabilityLoad;

        viewModel.NextCommand.Execute(null);
        await viewModel.AvailabilityLoad;
        late.SetResult(new QuickActionAvailability(true, true));
        await first;

        viewModel.DeleteCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies the texts follow a language change until the view model is disposed.
    /// </summary>
    [Fact]
    public async Task ViewModel_LanguageChange_RefreshesTextsUntilDisposed()
    {
        var localizer = TestLocalizer.English();
        var viewModel = new NewMailViewModel(Arrival("me@example.com", ("Hello", 1)), Dispatcher(), localizer, () => { });
        await viewModel.AvailabilityLoad;
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
    /// Verifies clicking the pop-up asks for the message that was shown, not the first one.
    /// </summary>
    [Fact]
    public void Notifier_OpenAfterNext_ShowsTheCurrentMessage()
    {
        var scenario = new Scenario();
        var arrival = Arrival("me@example.com", ("first", 1), ("second", 2));
        scenario.Events.RaiseNewMail(arrival);
        var popup = scenario.Presenter.Shown[0];
        var firstSubject = popup.Subject;
        popup.NextCommand.Execute(null);
        popup.Subject.Should().NotBe(firstSubject);
        var expected = arrival.Messages.Single(m => m.Subject == popup.Subject).MessageId;

        popup.OpenCommand.Execute(null);

        scenario.OpenedWith.Should().Be(expected);
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
    /// Verifies an action restarts the countdown of the pop-up and reports the change.
    /// </summary>
    [Fact]
    public async Task Notifier_ActionWithMoreMail_ShowsAgainAndReportsTheChange()
    {
        var scenario = new Scenario();
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("first", 1), ("second", 2)));
        var popup = scenario.Presenter.Shown[0];

        await popup.MarkReadCommand.ExecuteAsync(null);

        scenario.Presenter.Shown.Should().HaveCount(2);
        scenario.Presenter.Closed.Should().Be(0);
        scenario.Changed.Should().Be(1);
    }

    /// <summary>
    /// Verifies the pop-up disappears after the last message was handled.
    /// </summary>
    [Fact]
    public async Task Notifier_LastMessageHandled_ClosesPopup()
    {
        var scenario = new Scenario();
        scenario.Events.RaiseNewMail(Arrival("me@example.com", ("only", 1)));

        await scenario.Presenter.Shown[0].MarkReadCommand.ExecuteAsync(null);

        scenario.Presenter.Closed.Should().Be(1);
        scenario.Presenter.Shown.Should().ContainSingle("a closed pop-up must not be shown again");
        scenario.Opened.Should().Be(0);
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
            Notifier = new NewMailNotifier(
                Events, Settings, Dispatcher(), TestLocalizer.English(), UiThread, Presenter, id => { Opened++; OpenedWith = id; }, () => Changed++);
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

        /// <summary>
        /// Gets the message the last click asked to show.
        /// </summary>
        public Guid? OpenedWith { get; private set; }

        /// <summary>
        /// Gets how often a change of cached mail was reported.
        /// </summary>
        public int Changed { get; private set; }
    }
}
