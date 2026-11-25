using AwesomeAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Configures the headless Avalonia platform used by the UI smoke tests.
/// </summary>
public static class HeadlessEntry
{
    /// <summary>
    /// Builds the application with the Skia headless platform so frames can be rendered.
    /// </summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure(() => new App(new ServiceCollection().BuildServiceProvider()))
            .WithInterFont()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>
/// Loads the real windows in a headless Avalonia session to verify the XAML, compiled bindings and layout work together.
/// Set <c>SWIFTLET_UI_SNAPSHOT_DIR</c> to also write PNG snapshots of the rendered windows.
/// </summary>
public class UiSmokeTests
{
    /// <summary>
    /// The shared headless session; Avalonia can only be initialized once per process.
    /// </summary>
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

    /// <summary>
    /// Renders a window and optionally saves a snapshot.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="name">The snapshot file name without extension.</param>
    /// <param name="language">The UI language preference applied before showing the window.</param>
    private static void Render(Window window, string name, string language = "zh-Hans")
    {
        LocalizationManager.Instance.SetPreference(language);
        window.Show();
        Snapshot(window, name);
    }

    /// <summary>
    /// Renders pending changes and optionally saves a snapshot.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="name">The snapshot file name without extension.</param>
    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var directory = Environment.GetEnvironmentVariable("SWIFTLET_UI_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>
    /// Verifies the main window binds the folder tree, message list and reading pane.
    /// </summary>
    [Fact]
    public async Task MainWindow_WithSelectedMessage_ShowsTreeListAndReader()
    {
        await Session.Dispatch(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Messages[1].AddAttachment("quarterly-report.pdf", "application/pdf", 1_250_000);
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                scenario.ViewModel.StatusText = "同步完成，1 封新邮件";

                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window");

                window.GetVisualDescendants().OfType<TreeView>().Single().ItemCount.Should().Be(1);
                window.GetVisualDescendants().OfType<ListBox>().Single().ItemCount.Should().Be(2);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                texts.Should().Contain("Hello 2");
                texts.Should().Contain(t => t != null && t.Contains("line one", StringComparison.Ordinal));
                texts.Should().NotContain("选择一封邮件阅读");
                window.GetVisualDescendants().OfType<Button>()
                    .Should().Contain(b => (b.Content as string) != null && ((string)b.Content!).Contains("quarterly-report.pdf", StringComparison.Ordinal));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies every visible label of the main window follows a runtime language switch, without recreating the window.
    /// </summary>
    [Fact]
    public async Task MainWindow_LanguageSwitchedAtRuntime_RelabelsControls()
    {
        await Session.Dispatch(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-zh");
                var buttons = () => window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
                var labels = () => window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                buttons().Should().Contain("写邮件").And.Contain("同步");
                labels().Should().Contain("发件人：");

                LocalizationManager.Instance.SetPreference("en");
                scenario.Localizer.SetPreference("en");
                Snapshot(window, "main-window-en");

                buttons().Should().Contain("Compose").And.Contain("Sync").And.NotContain("写邮件");
                labels().Should().Contain("From:").And.Contain("Inbox").And.NotContain("发件人：");
                window.GetVisualDescendants().OfType<TextBox>().Select(t => t.PlaceholderText)
                    .Should().Contain("Search mail (press Enter)");
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the dialogs render in English.
    /// </summary>
    [Fact]
    public async Task Dialogs_English_ShowEnglishLabels()
    {
        await Session.Dispatch(
            () =>
            {
                var localizer = TestLocalizer.English();
                var addAccount = new AddAccountWindow { DataContext = new AddAccountViewModel(new FakeDispatcher(), localizer) { Email = "me@corp.example" } };
                Render(addAccount, "add-account-en", "en");
                addAccount.Title.Should().Be("Add mail account");
                addAccount.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string)
                    .Should().Contain("Server settings").And.Contain("Cancel").And.Contain("Add")
                    .And.Contain("Sign in with Google").And.Contain("Sign in with Microsoft");
                addAccount.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("or use a password");
                addAccount.Close();

                var account = TestData.Account();
                var request = new ComposeRequest(ComposeMode.New, account.Id, string.Empty, string.Empty, string.Empty, null);
                var compose = new ComposeWindow { DataContext = new ComposeViewModel(new FakeDispatcher(), new FakeDialogService(), localizer, [account], request) };
                Render(compose, "compose-en", "en");
                compose.Title.Should().Be("New message");
                compose.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                    .Should().Contain("To").And.Contain("Subject");
                compose.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string)
                    .Should().Contain("Send").And.Contain("Add attachment…");
                compose.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the reading pane shows its placeholder when nothing is selected.
    /// </summary>
    [Fact]
    public async Task MainWindow_NothingSelected_ShowsPlaceholder()
    {
        await Session.Dispatch(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();

                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-empty");

                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("选择一封邮件阅读");
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the add-account dialog reveals the server settings for an unknown provider.
    /// </summary>
    [Fact]
    public async Task AddAccountWindow_UnknownProvider_ShowsServerSettings()
    {
        await Session.Dispatch(
            () =>
            {
                var viewModel = new AddAccountViewModel(new FakeDispatcher(), TestLocalizer.Chinese()) { Email = "me@corp.example", Password = "secret" };
                var window = new AddAccountWindow { DataContext = viewModel };
                Render(window, "add-account");

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                texts.Should().Contain("IMAP").And.Contain("SMTP");
                texts.Should().Contain(t => t != null && t.Contains("未识别", StringComparison.Ordinal));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the compose window renders a reply with its fields filled.
    /// </summary>
    [Fact]
    public async Task ComposeWindow_Reply_ShowsPrefilledFields()
    {
        await Session.Dispatch(
            () =>
            {
                var account = TestData.Account();
                var request = new ComposeRequest(ComposeMode.Reply, account.Id, "alice@example.com", "Re: Hello", "\n\n> quoted", "<1@x.com>");
                var viewModel = new ComposeViewModel(new FakeDispatcher(), new FakeDialogService(), TestLocalizer.Chinese(), [account], request);
                var window = new ComposeWindow { DataContext = viewModel };
                Render(window, "compose");

                var boxes = window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text).ToList();
                boxes.Should().Contain("alice@example.com").And.Contain("Re: Hello");
                window.Title.Should().Be("回复");
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }
}
