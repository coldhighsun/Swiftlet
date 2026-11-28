using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.ViewModels;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="SplashViewModel"/>.
/// </summary>
public sealed class SplashViewModelTests
{
    /// <summary>
    /// Verifies the first step is shown before anything is reported.
    /// </summary>
    [Fact]
    public void Constructor_New_ShowsTheFirstStep()
    {
        var viewModel = new SplashViewModel(TestLocalizer.English(), new FakeUiThread());

        viewModel.Status.Should().Be("Checking mail data…");
        viewModel.Version.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Verifies every step has its text.
    /// </summary>
    /// <param name="stage">The step.</param>
    /// <param name="expected">The expected text.</param>
    [Theory]
    [InlineData(StartupStage.CheckingDatabase, "Checking mail data…")]
    [InlineData(StartupStage.OpeningDatabase, "Opening the mail database…")]
    [InlineData(StartupStage.OpeningAccount, "Opening account databases (2 of 4)…")]
    [InlineData(StartupStage.StartingServices, "Starting background services…")]
    [InlineData(StartupStage.LoadingWindow, "Loading the main window…")]
    public void Report_Step_ShowsItsText(StartupStage stage, string expected)
    {
        var uiThread = new FakeUiThread();
        var viewModel = new SplashViewModel(TestLocalizer.English(), uiThread);

        viewModel.Report(new StartupProgress(stage, 2, 4));

        viewModel.Status.Should().Be(expected);
        uiThread.Invocations.Should().ContainSingle();
    }
}
