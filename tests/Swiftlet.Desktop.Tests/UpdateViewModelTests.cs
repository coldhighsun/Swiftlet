using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Update;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the update window's view model and for the notifier.
/// </summary>
public class UpdateViewModelTests
{
    /// <summary>
    /// The update service.
    /// </summary>
    private readonly FakeUpdateService _updates = new() { Available = FakeUpdateService.Newer, CanInstall = true };

    /// <summary>
    /// Verifies the window asks first, offering to update, skip or wait.
    /// </summary>
    [Fact]
    public void Constructor_Installable_AsksWhetherToUpdate()
    {
        var viewModel = Create();

        viewModel.Header.Should().Be("Swiftlet 2.0.0 is available");
        viewModel.CurrentVersionText.Should().Be("You are using version 1.0.0.");
        viewModel.Notes.Should().Be("- Faster sync");
        viewModel.Step.Should().Be(UpdateStep.Prompt);
        viewModel.ShowUpdateButton.Should().BeTrue();
        viewModel.ShowSkipButton.Should().BeTrue();
        viewModel.ShowReleaseLink.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a copy that cannot update itself only points to the release page.
    /// </summary>
    [Fact]
    public void Constructor_NotInstallable_PointsToReleasePage()
    {
        _updates.CanInstall = false;

        var viewModel = Create();

        viewModel.ShowUpdateButton.Should().BeFalse();
        viewModel.ShowReleaseLink.Should().BeTrue();
        viewModel.InfoText.Should().Contain("release page");
    }

    /// <summary>
    /// Verifies very long release notes are cut.
    /// </summary>
    [Fact]
    public void Constructor_LongNotes_AreCut()
    {
        _updates.Available = FakeUpdateService.Newer with { Notes = new string('x', UpdateViewModel.MaxNotesLength + 10) };

        var viewModel = Create();

        viewModel.Notes.Should().HaveLength(UpdateViewModel.MaxNotesLength + 1).And.EndWith("…");
    }

    /// <summary>
    /// Verifies the progress is shown while downloading and the window closes once the installer runs.
    /// </summary>
    [Fact]
    public async Task UpdateCommand_Started_ShowsProgressThenCloses()
    {
        var install = new TaskCompletionSource<bool>();
        _updates.Install = _ => install.Task;
        var viewModel = Create();
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        var running = viewModel.UpdateCommand.ExecuteAsync(null);
        _updates.Progress = new UpdateProgress(512 * 1024, 1024 * 1024, 1024);
        _updates.RaiseProgress();

        viewModel.Step.Should().Be(UpdateStep.Downloading);
        viewModel.ProgressValue.Should().Be(50);
        viewModel.IsProgressIndeterminate.Should().BeFalse();
        viewModel.ShowLaterButton.Should().BeFalse();

        install.SetResult(true);
        await running;

        closed.Should().Be(1);
    }

    /// <summary>
    /// Verifies a failed installation is explained and can be retried.
    /// </summary>
    [Fact]
    public async Task UpdateCommand_Fails_ShowsErrorAndRetry()
    {
        _updates.Install = _ => Task.FromResult(false);
        _updates.InstallError = new InvalidOperationException("disk full");
        var viewModel = Create();

        await viewModel.UpdateCommand.ExecuteAsync(null);

        viewModel.Step.Should().Be(UpdateStep.Failed);
        viewModel.ErrorText.Should().Be("The update failed: disk full");
        viewModel.UpdateButtonText.Should().Be("Retry");
        viewModel.ShowReleaseLink.Should().BeTrue();
    }

    /// <summary>
    /// Verifies an unexpected exception does not leave the window downloading.
    /// </summary>
    [Fact]
    public async Task UpdateCommand_Throws_ShowsError()
    {
        _updates.Install = _ => Task.FromException<bool>(new IOException("broken"));
        var viewModel = Create();

        await viewModel.UpdateCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Be("The update failed: broken");
    }

    /// <summary>
    /// Verifies cancelling, or closing the window, stops the download.
    /// </summary>
    /// <param name="closeWindow">Whether the window is closed instead of the button pressed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDownload_Running_CancelsInstallation(bool closeWindow)
    {
        var cancelled = false;
        _updates.Install = async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        };
        var viewModel = Create();
        var running = viewModel.UpdateCommand.ExecuteAsync(null);

        if (closeWindow)
        {
            viewModel.Detach();
        }
        else
        {
            viewModel.CancelDownloadCommand.Execute(null);
        }

        await running;

        cancelled.Should().BeTrue();
        viewModel.Step.Should().Be(closeWindow ? UpdateStep.Downloading : UpdateStep.Prompt);
    }

    /// <summary>
    /// Verifies skipping records the choice and closes.
    /// </summary>
    [Fact]
    public async Task SkipCommand_Executed_SkipsAndCloses()
    {
        var viewModel = Create();
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        await viewModel.SkipCommand.ExecuteAsync(null);

        _updates.Skips.Should().Be(1);
        closed.Should().Be(1);
    }

    /// <summary>
    /// Verifies a visible main window gets the update window at once.
    /// </summary>
    [Fact]
    public void Announced_MainWindowShown_OpensDialog()
    {
        var dialogs = new FakeDialogService();
        var presenter = new FakeUpdateNoticePresenter();
        using var notifier = Notifier(dialogs, presenter, shown: true);

        _updates.RaiseAnnounced();

        dialogs.UpdateCalls.Should().Be(1);
        presenter.Shown.Should().BeNull();
    }

    /// <summary>
    /// Verifies a hidden main window gets a pop-up, which opens the update window when clicked.
    /// </summary>
    [Fact]
    public void Announced_MainWindowHidden_ShowsPopupThatOpensDialog()
    {
        var dialogs = new FakeDialogService();
        var presenter = new FakeUpdateNoticePresenter();
        using var notifier = Notifier(dialogs, presenter, shown: false);

        _updates.RaiseAnnounced();

        dialogs.UpdateCalls.Should().Be(0);
        presenter.Shown!.Text.Should().Contain("2.0.0");

        presenter.Shown.OpenCommand.Execute(null);

        dialogs.UpdateCalls.Should().Be(1);
        presenter.Shown.Should().BeNull();
    }

    /// <summary>
    /// Verifies the application quits once the installer runs.
    /// </summary>
    [Fact]
    public void InstallerStarted_Raised_Quits()
    {
        var quits = 0;
        using var notifier = Notifier(new FakeDialogService(), new FakeUpdateNoticePresenter(), shown: true, () => quits++);

        _updates.RaiseInstallerStarted();

        quits.Should().Be(1);
    }

    /// <summary>
    /// Verifies disposing stops the checks, closes the pop-up and ignores later events.
    /// </summary>
    [Fact]
    public async Task Dispose_Running_StopsEverything()
    {
        var dialogs = new FakeDialogService();
        var presenter = new FakeUpdateNoticePresenter();
        var notifier = Notifier(dialogs, presenter, shown: false);
        await notifier.RunAsync();
        _updates.RaiseAnnounced();
        var token = _updates.RunToken;

        notifier.Dispose();
        _updates.RaiseAnnounced();

        token.IsCancellationRequested.Should().BeTrue();
        presenter.Shown.Should().BeNull();
        dialogs.UpdateCalls.Should().Be(0);
    }

    /// <summary>
    /// Creates the view model for the offered version.
    /// </summary>
    /// <returns>The view model.</returns>
    private UpdateViewModel Create()
    {
        return new UpdateViewModel(_updates, _updates.Available!, "1.0.0", TestLocalizer.English());
    }

    /// <summary>
    /// Creates a notifier over the fakes.
    /// </summary>
    /// <param name="dialogs">The dialogs.</param>
    /// <param name="presenter">The pop-ups.</param>
    /// <param name="shown">Whether the main window is on screen.</param>
    /// <param name="quit">Ends the application; optional.</param>
    /// <returns>The notifier.</returns>
    private UpdateNotifier Notifier(
        IDialogService dialogs,
        IUpdateNoticePresenter presenter,
        bool shown,
        Action? quit = null)
    {
        return new UpdateNotifier(_updates, TestLocalizer.English(), dialogs, presenter, () => shown, quit ?? (() => { }));
    }
}
