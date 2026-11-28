using AwesomeAssertions;
using Avalonia;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Queries;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Desktop.Views;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the attachment hover preview: view model, hover controller, window placement and loading.
/// </summary>
public class AttachmentPreviewTests
{
    /// <summary>
    /// A short delay that keeps the tests fast.
    /// </summary>
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Creates an attachment view model.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>The attachment view model.</returns>
    private static AttachmentItemViewModel Attachment(string fileName = "notes.txt")
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1, "Hello");
        message.AddAttachment(fileName, "text/plain", 2048);
        return new AttachmentItemViewModel(account.Id, message.Attachments[0], _ => Task.CompletedTask, _ => Task.CompletedTask);
    }

    /// <summary>
    /// Creates a preview view model with the English localizer.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    /// <returns>The view model.</returns>
    private static AttachmentPreviewViewModel Preview(AttachmentItemViewModel attachment)
    {
        return new AttachmentPreviewViewModel(attachment, TestLocalizer.English());
    }

    /// <summary>
    /// Verifies a new preview shows the file and the loading message.
    /// </summary>
    [Fact]
    public void Constructor_NewPreview_ShowsFileAndLoadingMessage()
    {
        var viewModel = Preview(Attachment("report.txt"));

        viewModel.FileName.Should().Be("report.txt");
        viewModel.Details.Should().Be("2 KB");
        viewModel.Message.Should().Be("Loading preview…");
        viewModel.HasMessage.Should().BeTrue();
        viewModel.HasText.Should().BeFalse();
        viewModel.HasImage.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the preview tells that a click opens the file, or that a program can only be downloaded, also after the
    /// content has loaded.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="expected">The hint.</param>
    [Theory]
    [InlineData("report.txt", "Click the attachment to open it")]
    [InlineData("setup.exe", "Cannot be opened directly. Use the download button.")]
    public void ActionHint_FileType_TellsWhatAClickDoes(string fileName, string expected)
    {
        var viewModel = Preview(Attachment(fileName));

        viewModel.Apply(null);

        viewModel.ActionHint.Should().Be(expected);
    }

    /// <summary>
    /// Verifies a text preview replaces the loading message and marks cut text.
    /// </summary>
    [Fact]
    public void Apply_TruncatedText_ShowsTextWithEllipsis()
    {
        var viewModel = Preview(Attachment());

        viewModel.Apply(new AttachmentPreview(AttachmentPreviewKind.Text, Text: "line", Truncated: true));

        viewModel.Text.Should().Be("line\n…");
        viewModel.HasText.Should().BeTrue();
        viewModel.HasMessage.Should().BeFalse();
    }

    /// <summary>
    /// Verifies an archive listing tells how many entries are not shown.
    /// </summary>
    [Fact]
    public void Apply_ListingWithMoreEntries_AddsCount()
    {
        var viewModel = Preview(Attachment("a.zip"));

        viewModel.Apply(new AttachmentPreview(AttachmentPreviewKind.Listing, Text: "a.txt  (1 B)", MoreEntries: 5));

        viewModel.Text.Should().Be("a.txt  (1 B)\n… and 5 more");
    }

    /// <summary>
    /// Verifies the messages for the cases without content.
    /// </summary>
    /// <param name="kind">The preview kind; <see langword="null"/> means loading failed.</param>
    /// <param name="expected">The expected message.</param>
    [Theory]
    [InlineData(null, "No preview available")]
    [InlineData(AttachmentPreviewKind.Unsupported, "No preview available")]
    [InlineData(AttachmentPreviewKind.TooLarge, "File too large to preview")]
    public void Apply_NoContent_ShowsMessage(AttachmentPreviewKind? kind, string expected)
    {
        var viewModel = Preview(Attachment());

        viewModel.Apply(kind is null ? null : new AttachmentPreview(kind.Value));

        viewModel.Message.Should().Be(expected);
        viewModel.HasText.Should().BeFalse();
    }

    /// <summary>
    /// Verifies thumbnail bytes that cannot be decoded show the "not available" message instead of throwing.
    /// </summary>
    [Fact]
    public void Apply_UndecodableImage_ShowsMessage()
    {
        var viewModel = Preview(Attachment("a.png"));

        viewModel.Apply(new AttachmentPreview(AttachmentPreviewKind.Image, Image: [1, 2, 3]));

        viewModel.HasImage.Should().BeFalse();
        viewModel.Message.Should().Be("No preview available");
    }

    /// <summary>
    /// A presenter that records what it was asked to show.
    /// </summary>
    private sealed class RecordingPresenter : IAttachmentPreviewPresenter
    {
        /// <summary>
        /// Gets the view models shown, in order.
        /// </summary>
        public List<AttachmentPreviewViewModel> Shown { get; } = [];

        /// <summary>
        /// Gets how often the preview was hidden.
        /// </summary>
        public int HideCount { get; private set; }

        /// <inheritdoc />
        public void Show(AttachmentPreviewViewModel viewModel, object anchor)
        {
            Shown.Add(viewModel);
        }

        /// <inheritdoc />
        public void Hide()
        {
            HideCount++;
        }
    }

    /// <summary>
    /// Creates a controller whose loader returns the given task source's result.
    /// </summary>
    /// <param name="presenter">The recording presenter.</param>
    /// <param name="load">The loader.</param>
    /// <returns>The controller.</returns>
    private static AttachmentHoverController Controller(
        RecordingPresenter presenter,
        Func<AttachmentItemViewModel, CancellationToken, Task<AttachmentPreview?>> load)
    {
        return new AttachmentHoverController(load, Preview, presenter, ShortDelay);
    }

    /// <summary>
    /// Verifies the preview appears after the delay and then shows the loaded content.
    /// </summary>
    [Fact]
    public async Task Enter_PointerRests_ShowsLoadedPreview()
    {
        var presenter = new RecordingPresenter();
        var controller = Controller(
            presenter, (_, _) => Task.FromResult<AttachmentPreview?>(new AttachmentPreview(AttachmentPreviewKind.Text, Text: "hello")));

        controller.Enter(Attachment(), new object());
        await controller.Pending;

        presenter.Shown.Should().ContainSingle().Which.Text.Should().Be("hello");
    }

    /// <summary>
    /// Verifies nothing is shown or downloaded when the pointer leaves before the delay has passed.
    /// </summary>
    [Fact]
    public async Task Leave_BeforeDelay_ShowsAndLoadsNothing()
    {
        var presenter = new RecordingPresenter();
        var loads = 0;
        var controller = Controller(presenter, (_, _) =>
        {
            loads++;
            return Task.FromResult<AttachmentPreview?>(null);
        });

        controller.Enter(Attachment(), new object());
        controller.Leave();
        await controller.Pending;

        presenter.Shown.Should().BeEmpty();
        loads.Should().Be(0);
    }

    /// <summary>
    /// Verifies leaving while the preview loads hides it and drops the late result.
    /// </summary>
    [Fact]
    public async Task Leave_WhileLoading_HidesAndIgnoresResult()
    {
        var presenter = new RecordingPresenter();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<AttachmentPreview?>();
        var controller = Controller(presenter, async (_, token) =>
        {
            started.SetResult();
            return await release.Task.WaitAsync(token);
        });
        controller.Enter(Attachment(), new object());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        controller.Leave();
        release.TrySetResult(new AttachmentPreview(AttachmentPreviewKind.Text, Text: "late"));
        await controller.Pending;

        presenter.HideCount.Should().Be(1);
        presenter.Shown.Should().ContainSingle().Which.HasText.Should().BeFalse();
    }

    /// <summary>
    /// Verifies moving within the same attachment does not restart the request.
    /// </summary>
    [Fact]
    public async Task Enter_SameAttachmentAgain_LoadsOnce()
    {
        var presenter = new RecordingPresenter();
        var loads = 0;
        var controller = Controller(presenter, (_, _) =>
        {
            loads++;
            return Task.FromResult<AttachmentPreview?>(null);
        });
        var attachment = Attachment();

        controller.Enter(attachment, new object());
        controller.Enter(attachment, new object());
        await controller.Pending;

        loads.Should().Be(1);
    }

    /// <summary>
    /// Verifies entering another attachment cancels the first request and shows the second.
    /// </summary>
    [Fact]
    public async Task Enter_OtherAttachment_ReplacesPreview()
    {
        var presenter = new RecordingPresenter();
        var controller = Controller(
            presenter, (item, _) => Task.FromResult<AttachmentPreview?>(new AttachmentPreview(AttachmentPreviewKind.Text, Text: item.FileName)));

        controller.Enter(Attachment("first.txt"), new object());
        controller.Enter(Attachment("second.txt"), new object());
        await controller.Pending;

        presenter.Shown.Should().ContainSingle().Which.Text.Should().Be("second.txt");
    }

    /// <summary>
    /// Verifies the preview goes below the attachment when there is room.
    /// </summary>
    [Fact]
    public void Place_RoomBelow_PlacesBelowAnchor()
    {
        var position = AttachmentPreviewWindow.Place(
            new PixelRect(0, 0, 1920, 1040), new PixelRect(200, 300, 100, 40), new PixelSize(360, 200), 6);

        position.Should().Be(new PixelPoint(200, 346));
    }

    /// <summary>
    /// Verifies the preview goes above the attachment near the bottom of the screen.
    /// </summary>
    [Fact]
    public void Place_NoRoomBelow_PlacesAboveAnchor()
    {
        var position = AttachmentPreviewWindow.Place(
            new PixelRect(0, 0, 1920, 1040), new PixelRect(200, 950, 100, 40), new PixelSize(360, 200), 6);

        position.Should().Be(new PixelPoint(200, 744));
    }

    /// <summary>
    /// Verifies the preview stays inside the screen horizontally.
    /// </summary>
    [Fact]
    public void Place_AnchorNearRightEdge_ShiftsLeft()
    {
        var position = AttachmentPreviewWindow.Place(
            new PixelRect(0, 0, 1920, 1040), new PixelRect(1850, 300, 60, 40), new PixelSize(360, 200), 6);

        position.X.Should().Be(1920 - 360);
    }

    /// <summary>
    /// Verifies a failed download is reported as no preview and does not touch the status line.
    /// </summary>
    [Fact]
    public async Task LoadAttachmentPreviewAsync_ServerFails_ReturnsNullWithoutStatusError()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Messages[1].AddAttachment("notes.txt", "text/plain", 5);
        scenario.Dispatcher.On<GetAttachmentPreviewQuery>(_ => throw new InvalidOperationException("offline"));
        await scenario.StartAsync();
        var attachment = scenario.ViewModel.Messages[0].Attachments.Should().ContainSingle().Subject;

        var preview = await scenario.ViewModel.LoadAttachmentPreviewAsync(attachment, TestContext.Current.CancellationToken);

        preview.Should().BeNull();
        scenario.ViewModel.StatusText.Should().NotContain("offline");
    }

    /// <summary>
    /// Verifies the query for the hovered attachment is sent and its preview returned.
    /// </summary>
    [Fact]
    public async Task LoadAttachmentPreviewAsync_Available_SendsQueryAndReturnsPreview()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Messages[1].AddAttachment("notes.txt", "text/plain", 5);
        var expected = new AttachmentPreview(AttachmentPreviewKind.Text, Text: "hello");
        scenario.Dispatcher.On<GetAttachmentPreviewQuery>(_ => expected);
        await scenario.StartAsync();
        var attachment = scenario.ViewModel.Messages[0].Attachments.Should().ContainSingle().Subject;

        var preview = await scenario.ViewModel.LoadAttachmentPreviewAsync(attachment, TestContext.Current.CancellationToken);

        preview.Should().Be(expected);
        scenario.Dispatcher.Sent<GetAttachmentPreviewQuery>().Should().ContainSingle()
            .Which.AttachmentId.Should().Be(attachment.Attachment.Id);
    }
}
