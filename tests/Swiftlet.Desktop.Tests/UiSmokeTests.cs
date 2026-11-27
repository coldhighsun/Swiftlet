using AwesomeAssertions;
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Desktop.Controls;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Desktop.ViewModels.Contacts;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Desktop.ViewModels.Settings;
using Swiftlet.Desktop.ViewModels.Update;
using Swiftlet.Desktop.Views;

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
    internal static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

    /// <summary>
    /// Runs an asynchronous test body on the UI thread and waits for all of it. Passing an async lambda straight to
    /// <see cref="HeadlessUnitTestSession.Dispatch{TResult}(Func{TResult}, CancellationToken)"/> only starts it, so a
    /// failed assertion would be lost; returning a value selects the overload that awaits.
    /// </summary>
    /// <param name="body">The test body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the body has completed.</returns>
    private static Task<bool> DispatchAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        return Session.Dispatch(
            async () =>
            {
                await body();
                return true;
            },
            cancellationToken);
    }

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
    /// Verifies showing a window that was hidden in the tray does not load the data again.
    /// </summary>
    [Fact]
    public async Task MainWindow_HiddenAndShownAgain_InitializesOnce()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario(hasAccount: false);
                var window = new MainWindow { DataContext = scenario.ViewModel };
                var activator = new WindowActivator(window);
                window.Show();
                await scenario.ViewModel.WhenIdleAsync();

                window.Hide();
                window.WindowState = WindowState.Minimized;
                activator.BringToFront();
                Dispatcher.UIThread.RunJobs();

                window.IsVisible.Should().BeTrue();
                window.WindowState.Should().Be(WindowState.Normal);
                scenario.Dialogs.AddAccountCalls.Should().Be(1);
                window.Close();

                // A closed window cannot be shown again, so a late request is ignored.
                activator.BringToFront();
                window.IsVisible.Should().BeFalse();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies closing from code ends the window even while it is set to stay in the tray.
    /// </summary>
    [Fact]
    public async Task CloseToTray_ClosedByCode_Closes()
    {
        await DispatchAsync(
            () =>
            {
                var window = new Window();
                var closed = false;
                window.Closed += (_, _) => closed = true;
                CloseToTray.Attach(window, () => true);
                window.Show();

                window.Close();

                closed.Should().BeTrue();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a window moved and resized comes back there after being hidden in the tray, even when it was moved
    /// while hidden, and the placement is saved.
    /// </summary>
    [Fact]
    public async Task WindowPlacement_ClosedToTrayAndShownAgain_RestoresBounds()
    {
        await DispatchAsync(
            () =>
            {
                var settings = new FakeAppSettings();
                var window = new Window { Width = 1000, Height = 700 };
                RememberWindowPlacement.Attach(window, settings);
                window.Show();
                window.Position = new PixelPoint(120, 80);
                window.Width = 900;
                window.Height = 600;
                Dispatcher.UIThread.RunJobs();

                window.Hide();
                window.Position = new PixelPoint(5, 5);
                window.Width = 400;
                Dispatcher.UIThread.RunJobs();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                window.Position.Should().Be(new PixelPoint(120, 80));
                window.ClientSize.Should().Be(new Size(900, 600));
                window.Close();
                settings.MainWindowPlacement.Should().Be(new Swiftlet.Desktop.Settings.WindowPlacement(120, 80, 900, 600, false));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the saved placement is applied the first time, and a maximized window is saved with the bounds it
    /// returns to.
    /// </summary>
    [Fact]
    public async Task WindowPlacement_SavedMaximized_AppliesAndKeepsNormalBounds()
    {
        await DispatchAsync(
            () =>
            {
                var saved = new Swiftlet.Desktop.Settings.WindowPlacement(60, 40, 900, 600, true);
                var settings = new FakeAppSettings { MainWindowPlacement = saved };
                var window = new Window { Width = 1000, Height = 700 };

                RememberWindowPlacement.Attach(window, settings);
                window.Position.Should().Be(new PixelPoint(60, 40));
                window.Width.Should().Be(900);
                window.Show();
                Dispatcher.UIThread.RunJobs();

                window.WindowState.Should().Be(WindowState.Maximized);
                window.Close();
                settings.MainWindowPlacement.Should().Be(saved);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a placement that cannot be saved does not stop the window from closing.
    /// </summary>
    [Fact]
    public async Task WindowPlacement_SaveFails_WindowStillCloses()
    {
        await DispatchAsync(
            () =>
            {
                var settings = new FakeAppSettings { Failure = new IOException("disk full") };
                var window = new Window { Width = 900, Height = 600 };
                var closed = false;
                window.Closed += (_, _) => closed = true;
                RememberWindowPlacement.Attach(window, settings);
                window.Show();
                Dispatcher.UIThread.RunJobs();

                window.Close();

                closed.Should().BeTrue();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the tray icon carries the menu and follows the language.
    /// </summary>
    [Fact]
    public async Task TrayIcon_Created_HasMenuInTheCurrentLanguage()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                using var viewModel = new TrayViewModel(scenario.ViewModel, scenario.Localizer, new FakeAppSettings(), () => { }, () => { });
                using var tray = new AvaloniaTrayIcon(Avalonia.Application.Current!, viewModel);
                var items = tray.Icon.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();

                items.Select(i => i.Header).Should().Equal("打开 Swiftlet", "写邮件", "同步全部账户", "设置", "退出");
                tray.Icon.ToolTipText.Should().Be(viewModel.ToolTipText);
                tray.Icon.Command.Should().BeSameAs(viewModel.OpenCommand);

                scenario.Localizer.SetPreference("en");

                items.Select(i => i.Header).Should().Equal("Open Swiftlet", "Compose", "Sync all accounts", "Settings", "Quit");
                tray.Icon.ToolTipText.Should().StartWith("Swiftlet - Unread: 2");
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the icon changes its picture with the unread mail and blinks only in that style.
    /// </summary>
    [Fact]
    public async Task TrayIcon_UnreadMail_ShowsDotOrBlinks()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                var settings = new FakeAppSettings();
                using var viewModel = new TrayViewModel(scenario.ViewModel, scenario.Localizer, settings, () => { }, () => { });
                using var tray = new AvaloniaTrayIcon(Avalonia.Application.Current!, viewModel);
                tray.Icon.Icon.Should().BeSameAs(tray.NormalImage);

                await scenario.StartAsync();

                tray.Icon.Icon.Should().BeSameAs(tray.UnreadImage);
                tray.IsBlinking.Should().BeFalse();

                settings.SetTrayUnreadStyle("blink");

                tray.IsBlinking.Should().BeTrue();
                tray.Icon.Icon.Should().BeSameAs(tray.UnreadImage);
                tray.Blink();
                tray.Icon.Icon.Should().BeSameAs(tray.NormalImage);
                tray.Blink();
                tray.Icon.Icon.Should().BeSameAs(tray.UnreadImage);

                tray.Blink();
                viewModel.Indicator = TrayIndicator.None;

                tray.IsBlinking.Should().BeFalse();
                tray.Icon.Icon.Should().BeSameAs(tray.NormalImage);
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the settings open on their own while the main window is not on screen, only once, and again after closing.
    /// </summary>
    /// <param name="minimized">Whether the main window is minimized rather than hidden.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowSettings_MainWindowNotOnScreen_OpensOneStandaloneWindow(bool minimized)
    {
        await DispatchAsync(
            async () =>
            {
                var created = new List<SettingsViewModel>();
                var services = new ServiceCollection()
                    .AddTransient(_ =>
                    {
                        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), new FakeRemoteImageRules());
                        created.Add(viewModel);
                        return viewModel;
                    })
                    .BuildServiceProvider();
                var dialogs = new AvaloniaDialogService(services);
                var main = new Window();
                if (minimized)
                {
                    main.Show();
                    main.WindowState = WindowState.Minimized;
                }

                var first = dialogs.ShowSettingsAsync(main);
                var again = dialogs.ShowSettingsAsync(main);

                again.IsCompletedSuccessfully.Should().BeTrue("the open window is only brought to the front");
                first.IsCompleted.Should().BeFalse();
                created.Should().ContainSingle();
                main.IsVisible.Should().Be(minimized);

                created[0].CloseCommand.Execute(null);
                await first;
                var next = dialogs.ShowSettingsAsync(main);

                created.Should().HaveCount(2);
                created[1].CloseCommand.Execute(null);
                await next;
                main.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the main window binds the folder tree, message list and reading pane.
    /// </summary>
    [Fact]
    public async Task MainWindow_WithSelectedMessage_ShowsTreeListAndReader()
    {
        await DispatchAsync(
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

                window.GetVisualDescendants().OfType<TreeView>().Single().ItemCount.Should().Be(3, "the unified inbox, the account and the label");
                window.GetVisualDescendants().OfType<ListBox>().Single().ItemCount.Should().Be(2);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                texts.Should().Contain("Hello 2");
                texts.Should().Contain(t => t != null && t.Contains("line one", StringComparison.Ordinal));
                window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "选择一封邮件阅读")
                    .Should().OnlyContain(t => !t.IsEffectivelyVisible);
                UiQueries.ButtonCaptions(window).Should().Contain(c => c != null && c.Contains("quarterly-report.pdf", StringComparison.Ordinal));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the reading pane hosts the HTML view (and hides the plain-text body) for a message with an HTML body.
    /// </summary>
    [Fact]
    public async Task MainWindow_HtmlMessageSelected_ShowsHtmlBodyView()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<p>rich</p>");
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();

                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-html");

                var htmlView = window.GetVisualDescendants().OfType<Swiftlet.Desktop.Controls.HtmlBodyView>().Single();
                htmlView.IsVisible.Should().BeTrue();
                htmlView.Document.Should().Contain("<p>rich</p>");
                window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Content is SelectableTextBlock).IsVisible.Should().BeFalse();
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
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-zh");
                var buttons = () => UiQueries.ButtonCaptions(window);
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
                    .And.Contain("Sign in with Microsoft");
                addAccount.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content as string)
                    .Should().NotContain("Sign in with Google");
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
    /// Verifies the startup window explains a lost key and offers the reset, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="reset">The expected reset button text.</param>
    [Theory]
    [InlineData("en", "Reset local data…")]
    [InlineData("zh-Hans", "重置本地数据…")]
    public async Task StartupWindow_KeyLost_ShowsResetButton(string language, string reset)
    {
        await DispatchAsync(
            async () =>
            {
                var protection = new FakeDatabaseProtection();
                protection.Results.Enqueue(new Swiftlet.Infrastructure.Persistence.Encryption.DatabaseProtectionResult(
                    Swiftlet.Infrastructure.Persistence.Encryption.DatabaseProtectionProblem.KeyLost));
                var localizer = language == "en" ? TestLocalizer.English() : TestLocalizer.Chinese();
                var viewModel = new StartupViewModel(
                    protection, localizer, new FakeDialogService(), new FakeUiThread(), _ => Task.FromResult<string?>(null));
                var window = new StartupWindow { DataContext = viewModel };
                LocalizationManager.Instance.SetPreference(language);
                await viewModel.RunCommand.ExecuteAsync(null);

                Render(window, $"startup-key-lost-{language}", language);

                var visible = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
                visible.Should().HaveCount(3).And.Contain(reset);
                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain(viewModel.Message);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the reading pane shows its placeholder when nothing is selected.
    /// </summary>
    [Fact]
    public async Task MainWindow_NothingSelected_ShowsPlaceholder()
    {
        await DispatchAsync(
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
    /// Verifies the add-account dialog links an address to its provider's app password page, without an underline.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="provider">The mail domain without its suffix.</param>
    /// <param name="caption">The expected link text.</param>
    /// <param name="page">The expected page.</param>
    [Theory]
    [InlineData("en", "gmail", "Create a Gmail app password (requires 2-Step Verification)", "https://myaccount.google.com/apppasswords")]
    [InlineData("zh-Hans", "gmail", "生成 Gmail 应用专用密码（需先开启两步验证）", "https://myaccount.google.com/apppasswords")]
    [InlineData("en", "icloud", "Create an iCloud app-specific password (requires two-factor authentication)", "https://account.apple.com/")]
    [InlineData("zh-Hans", "icloud", "生成 iCloud App 专用密码（需先开启双重认证）", "https://account.apple.com/")]
    public async Task AddAccountWindow_AppPasswordProvider_ShowsLink(string language, string provider, string caption, string page)
    {
        await Session.Dispatch(
            () =>
            {
                var localizer = language == "en" ? TestLocalizer.English() : TestLocalizer.Chinese();
                var viewModel = new AddAccountViewModel(new FakeDispatcher(), localizer) { Email = $"me@{provider}.com" };
                var window = new AddAccountWindow { DataContext = viewModel };
                Render(window, $"add-account-{provider}-{language}", language);

                var link = window.GetVisualDescendants().OfType<HyperlinkButton>().Single();
                link.IsVisible.Should().BeTrue();
                link.Content.Should().Be(caption);
                link.NavigateUri.Should().Be(new Uri(page));
                link.GetVisualDescendants().OfType<TextBlock>().Single().TextDecorations.Should().BeNullOrEmpty();

                viewModel.Email = "me@qq.com";
                Dispatcher.UIThread.RunJobs();
                link.IsVisible.Should().BeFalse();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("en", false, "Save")]
    [InlineData("zh-Hans", false, "保存")]
    [InlineData("en", true, "Save")]
    [InlineData("zh-Hans", true, "保存")]
    public async Task EditAccountWindow_ShowsServerSettingsOnlyForPasswordAccounts(string language, bool oauth, string save)
    {
        await Session.Dispatch(
            () =>
            {
                var localizer = language == "en" ? TestLocalizer.English() : TestLocalizer.Chinese();
                LocalizationManager.Instance.SetPreference(language);
                var viewModel = new EditAccountViewModel(new FakeDispatcher(), localizer);
                var account = oauth ? TestData.OAuthAccount() : TestData.Account();
                viewModel.Load(account, [TestData.Folder(account, "Archive", Swiftlet.Domain.Folders.FolderRole.Archive)]);
                var window = new EditAccountWindow { DataContext = viewModel };
                Render(window, $"edit-account-{(oauth ? "oauth" : "password")}-{language}", language);

                var visible = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                visible.Should().Contain(viewModel.Email);
                visible.Contains("IMAP").Should().Be(!oauth);
                window.GetVisualDescendants().OfType<TextBox>().Count(t => t.IsEffectivelyVisible).Should().Be(oauth ? 1 : 6);
                window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).Should().Contain(save);
                window.GetVisualDescendants().OfType<ComboBox>().Select(c => c.SelectedItem).Should()
                    .Contain(viewModel.ArchiveFolderOptions[0]);
                visible.Should().Contain(localizer["EditAccount_ArchiveFolder"]);
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

    /// <summary>
    /// Verifies the tree shows the unified inbox, accounts and labels, and the toolbar has a delete button, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="unifiedInbox">The expected unified inbox caption.</param>
    /// <param name="delete">The expected delete button caption.</param>
    [Theory]
    [InlineData("zh-Hans", "统一收件箱", "删除")]
    [InlineData("en", "Unified Inbox", "Delete")]
    public async Task MainWindow_WithUnifiedInboxAndLabels_Renders(string language, string unifiedInbox, string delete)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference(language);
                scenario.Messages[0].AddLabel(scenario.Work);
                await scenario.StartAsync();
                scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots[0];
                await scenario.ViewModel.WhenIdleAsync();

                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, $"main-window-labels-{language}", language);

                window.GetVisualDescendants().OfType<TreeView>().Single().ItemCount.Should().Be(3);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                texts.Should().Contain(unifiedInbox).And.Contain("Work");
                UiQueries.ButtonCaptions(window).Should().Contain(delete);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a message with remote images shows the privacy banner with a working load button, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="notice">The expected banner text.</param>
    /// <param name="loadButton">The expected button caption.</param>
    [Theory]
    [InlineData("zh-Hans", "为保护隐私，已阻止加载远程图片。", "加载图片")]
    [InlineData("en", "Remote images were blocked to protect your privacy.", "Load images")]
    public async Task MainWindow_RemoteImagesBlocked_ShowsBannerUntilLoaded(string language, string notice, string loadButton)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference(language);
                scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<img src=\"https://x.example/a.png\">");
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();

                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, $"main-window-remote-images-{language}", language);

                var banner = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == notice);
                banner.IsEffectivelyVisible.Should().BeTrue();
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == loadButton);
                button.Command!.Execute(null);
                Render(window, $"main-window-remote-images-loaded-{language}", language);

                banner.IsEffectivelyVisible.Should().BeFalse();
                scenario.ViewModel.SelectedMessage!.AllowRemoteImages.Should().BeTrue();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the banner's load button is a split button whose drop-down offers the two "always allow" choices, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="loadButton">The expected caption of the main button.</param>
    /// <param name="senderText">The expected text of the "always allow this sender" choice.</param>
    /// <param name="domainText">The expected text of the "always allow this domain" choice.</param>
    [Theory]
    [InlineData("zh-Hans", "加载图片", "总是加载 alice@example.com 的图片", "总是加载 example.com 域发件人的图片")]
    [InlineData("en", "Load images", "Always load images from alice@example.com", "Always load images from senders at example.com")]
    public async Task MainWindow_RemoteImagesBlocked_SplitButtonOffersAlwaysAllowChoices(
        string language,
        string loadButton,
        string senderText,
        string domainText)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference(language);
                scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<img src=\"https://x.example/a.png\">");
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();

                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, $"main-window-split-button-{language}", language);

                var split = window.GetVisualDescendants().OfType<SplitButton>().Single();
                split.IsEffectivelyVisible.Should().BeTrue();
                split.Content.Should().Be(loadButton);
                var menu = split.Flyout.Should().BeOfType<MenuFlyout>().Subject;
                menu.Items.OfType<MenuItem>().Should().HaveCount(2);
                var selected = scenario.ViewModel.SelectedMessage!;
                selected.AlwaysAllowSenderText.Should().Be(senderText);
                selected.AlwaysAllowDomainText.Should().Be(domainText);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the settings window lists the saved remote image rules with the count and the editing controls, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="heading">The expected section heading.</param>
    /// <param name="count">The expected count text.</param>
    /// <param name="add">The expected caption of the add button.</param>
    /// <param name="remove">The expected caption of the remove button.</param>
    [Theory]
    [InlineData("zh-Hans", "远程图片", "显示 3 / 共 3 条", "添加", "删除所选")]
    [InlineData("en", "Remote images", "Showing 3 of 3", "Add", "Remove selected")]
    public async Task SettingsWindow_RemoteImageRules_ShowsListCountAndButtons(
        string language,
        string heading,
        string count,
        string add,
        string remove)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var rules = new FakeRemoteImageRules();
                foreach (var rule in (string[])["shop.com", "news@other.org", "promo.example.net"])
                {
                    await rules.AddAsync(rule, TestContext.Current.CancellationToken);
                }

                var viewModel = new SettingsViewModel(new FakeAppSettings(), LocalizationManager.Instance, rules);
                var window = new SettingsWindow { DataContext = viewModel };
                Render(window, $"settings-remote-images-{language}", language);
                SelectSettingsPage(window, 2, $"settings-remote-images-{language}");

                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                list.ItemCount.Should().Be(3);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                texts.Should().Contain(heading);
                texts.Should().Contain(count);
                var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
                buttons.Should().Contain([add, remove]);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the label manager lists labels with their editing controls, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="title">The expected window title.</param>
    /// <param name="add">The expected add button caption.</param>
    /// <param name="save">The expected save button caption.</param>
    [Theory]
    [InlineData("zh-Hans", "管理标签", "添加", "保存")]
    [InlineData("en", "Manage labels", "Add", "Save")]
    public async Task LabelManagerWindow_Renders(string language, string title, string add, string save)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var dispatcher = new FakeDispatcher();
                dispatcher.On<Swiftlet.Application.Labels.GetLabelsQuery>(_ => (IReadOnlyList<Swiftlet.Domain.Labels.Label>)new List<Swiftlet.Domain.Labels.Label>
                {
                    Swiftlet.Domain.Labels.Label.Create("Travel", "#1E88E5"),
                    Swiftlet.Domain.Labels.Label.Create("Work", "#E53935"),
                });
                var viewModel = new LabelManagerViewModel(dispatcher, LocalizationManager.Instance) { NewName = "Receipts" };
                await viewModel.InitializeAsync();
                viewModel.ErrorText = LocalizationManager.Instance["Labels_ErrorInvalid"];
                var window = new LabelManagerWindow { DataContext = viewModel };
                Render(window, $"label-manager-{language}", language);

                window.Title.Should().Be(title);
                var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
                buttons.Should().Contain(add).And.Contain(save);
                window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text).Should().Contain(["Receipts", "Travel", "Work"]);
                window.GetVisualDescendants().OfType<ComboBox>().Should().HaveCount(3);
                window.GetVisualDescendants().OfType<Button>().Count(b => b.Command == viewModel.DeleteCommand).Should().Be(2);
                window.GetVisualDescendants().OfType<Button>().Where(b => b.Command == viewModel.DeleteCommand).Should()
                    .OnlyContain(b => b.Content is PathIcon && b.Classes.Contains("danger")
                        && UiQueries.Caption(b) == LocalizationManager.Instance["Labels_Delete"] && ToolTip.GetTip(b) != null);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the contacts window shows the card of a contact, the form editing it, and the collected addresses with
    /// the actions for one and for several of them, in both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="title">The expected window title.</param>
    /// <param name="add">The expected caption of the button adding collected addresses.</param>
    [Theory]
    [InlineData("zh-Hans", "通讯录", "加入联系人")]
    [InlineData("en", "Contacts", "Add to contacts")]
    public async Task ContactsWindow_Renders(string language, string title, string add)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var viewModel = await CreateContactsAsync(LocalizationManager.Instance);
                var window = new ContactsWindow { DataContext = viewModel };
                Render(window, $"contacts-{language}", language);
                viewModel.SelectedContact = viewModel.Contacts.Single(c => c.Contact.Emails.Count == 2);
                Snapshot(window, $"contacts-card-{language}");

                window.Title.Should().Be(title);
                List<Button> Visible() => window.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible).ToList();
                Visible().Where(b => b.Command == viewModel.CopyCommand).Should().HaveCount(3)
                    .And.OnlyContain(b => ToolTip.GetTip(b) != null);
                Visible().Where(b => b.Command == viewModel.WriteCommand && b.Content is PathIcon).Should().HaveCount(2);
                window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible)
                    .Should().ContainSingle("only the search box can be typed in while viewing");

                viewModel.EditCommand.Execute(null);
                Snapshot(window, $"contacts-edit-{language}");

                var boxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).ToList();
                boxes.Select(t => t.Text).Should().Contain(["张", "三", "zhang@example.com", "zhangsan@work.example.com"]);
                var familyName = boxes.Single(t => t.Text == "张").TranslatePoint(default, window)!.Value.X;
                var givenName = boxes.Single(t => t.Text == "三").TranslatePoint(default, window)!.Value.X;
                (familyName < givenName).Should().Be(language == "zh-Hans", "Chinese names put the family name first");
                Visible().Where(b => viewModel.Emails.Any(f => f.RemoveCommand == b.Command)).Should().HaveCount(2)
                    .And.OnlyContain(b => ToolTip.GetTip(b) != null);
                Visible().Where(b => viewModel.Emails.Any(f => f.MakePrimaryCommand == b.Command)).Should().HaveCount(2)
                    .And.OnlyContain(b => ToolTip.GetTip(b) != null);
                Visible().Should().Contain(b => b.Command == viewModel.SaveCommand)
                    .And.Contain(b => b.Command == viewModel.CancelCommand);

                await viewModel.CancelCommand.ExecuteAsync(null);
                viewModel.SelectedPage = 1;
                Snapshot(window, $"contacts-collected-empty-selection-{language}");
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.IsEffectivelyVisible);
                list.SelectedIndex = 0;
                Snapshot(window, $"contacts-collected-one-{language}");

                viewModel.SelectedCollected.Should().ContainSingle();
                Visible().Should().ContainSingle(b => b.Command == viewModel.WriteCollectedCommand);

                list.SelectAll();
                Snapshot(window, $"contacts-collected-many-{language}");

                viewModel.SelectedCollected.Should().HaveCount(2);
                Visible().Should().ContainSingle(b => b.Command == viewModel.AddSelectedCollectedCommand)
                    .Which.Content.Should().Be(add);
                Visible().Should().NotContain(b => b.Command == viewModel.WriteCollectedCommand);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies typing a recipient opens the suggestions, and choosing one with the keyboard completes the entry.
    /// </summary>
    [Fact]
    public async Task ComposeWindow_TypingRecipient_SuggestsAndCompletes()
    {
        await DispatchAsync(
            () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var account = TestData.Account();
                var viewModel = new ComposeViewModel(
                    new FakeDispatcher(),
                    new FakeDialogService(),
                    TestLocalizer.English(),
                    [account],
                    new ComposeRequest(ComposeMode.New, account.Id, string.Empty, string.Empty, string.Empty, null))
                {
                    Suggestions = [new("Alice", "alice@example.com"), new("Bob", "bob@example.com")],
                };
                var window = new ComposeWindow { DataContext = viewModel };
                Render(window, "compose-suggestions-closed", "en");
                var box = window.GetVisualDescendants().OfType<RecipientBox>().First();
                var input = box.GetVisualDescendants().OfType<TextBox>().Single();
                input.Focus();
                Dispatcher.UIThread.RunJobs();

                window.KeyTextInput("carol@example.com, ali");
                Dispatcher.UIThread.RunJobs();

                box.IsDropDownOpen.Should().BeTrue();
                Snapshot(window, "compose-suggestions-open");

                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();

                viewModel.To.Should().Be("carol@example.com, Alice <alice@example.com>, ");
                input.CaretIndex.Should().Be(viewModel.To.Length);
                box.IsDropDownOpen.Should().BeFalse();

                window.KeyTextInput("bo");
                Dispatcher.UIThread.RunJobs();

                viewModel.To.Should().Be("carol@example.com, Alice <alice@example.com>, bo");
                box.IsDropDownOpen.Should().BeTrue();
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the settings window shows the pickers, the checkboxes, their hints and the close button on their pages, in
    /// both languages.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="title">The expected window title.</param>
    /// <param name="checkbox">The expected checkbox caption.</param>
    /// <param name="close">The expected close button caption.</param>
    [Theory]
    [InlineData("zh-Hans", "设置", "删除服务器上已删除的邮件的本地副本", "关闭")]
    [InlineData("en", "Settings", "Remove local messages that were deleted on the server", "Close")]
    public async Task SettingsWindow_Renders(string language, string title, string checkbox, string close)
    {
        await Session.Dispatch(
            () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var selector = new LanguageSelectorViewModel(
                    LocalizationManager.Instance,
                    new FakeLanguageStore(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<LanguageSelectorViewModel>.Instance);
                var viewModel = new SettingsViewModel(
                    new FakeAppSettings(), LocalizationManager.Instance, new FakeRemoteImageRules(), languageSelector: selector);
                var window = new SettingsWindow { DataContext = viewModel };
                Render(window, $"settings-general-{language}", language);

                window.Title.Should().Be(title);
                window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible)
                    .Select(c => c.SelectedItem).Should().Equal(
                        selector.SelectedOption,
                        viewModel.SelectedTheme,
                        viewModel.SelectedTrayUnreadStyle,
                        viewModel.SelectedContactNameOrder);
                var generalBoxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
                generalBoxes.Should().HaveCount(3);
                generalBoxes.Take(2).Should().OnlyContain(b => b.IsChecked == true, "the tray and notification settings are on by default");
                generalBoxes[2].IsChecked.Should().BeFalse("the hidden start is off by default");
                window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).Should().Contain(close);

                SelectSettingsPage(window, 1, $"settings-sync-{language}");

                var boxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
                boxes.Should().HaveCount(2);
                var box = boxes.Single(b => b.Content as string == checkbox);
                box.IsChecked.Should().BeFalse();
                boxes.Where(b => b != box).Should().OnlyContain(b => b.IsChecked == true, "the history download is on by default");
                window.GetVisualDescendants().OfType<TextBlock>().Count(t => !string.IsNullOrEmpty(t.Text) && t.Text.Length > 40).Should().BeGreaterThan(0);
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the sidebar names every page and the "About" page shows the version, the links and the folder buttons,
    /// also at the smallest window size.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="pages">The expected names of the pages, separated by '|'.</param>
    /// <param name="openLogs">The expected caption of the log folder button.</param>
    [Theory]
    [InlineData("zh-Hans", "通用|同步|隐私与安全|AI|关于", "打开日志目录")]
    [InlineData("en", "General|Sync|Privacy & security|AI|About", "Open log folder")]
    public async Task SettingsWindow_AboutPage_ShowsVersionLinksAndFolders(string language, string pages, string openLogs)
    {
        await Session.Dispatch(
            () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var about = new AboutViewModel(
                    new FakeDialogService(), LocalizationManager.Instance, new Swiftlet.Desktop.Hosting.AppPaths("data"));
                var viewModel = new SettingsViewModel(
                    new FakeAppSettings(),
                    LocalizationManager.Instance,
                    new FakeRemoteImageRules(),
                    about: about,
                    ai: CreateAiPage());
                var window = new SettingsWindow { DataContext = viewModel };
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Render(window, $"settings-minimum-{language}", language);

                var items = window.GetVisualDescendants().OfType<TabItem>().ToList();
                items.Select(Avalonia.Automation.AutomationProperties.GetName).Should().Equal(pages.Split('|'));
                items.Should().OnlyContain(i => i.IsEffectivelyVisible && i.Bounds.Width > 0);

                SelectSettingsPage(window, 3, $"settings-ai-{language}");

                window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible)
                    .Select(c => c.IsEditable).Should().Equal(false, true);
                window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEditable && c.IsEffectivelyVisible)
                    .Text.Should().Be("claude-opus-5-5");

                SelectSettingsPage(window, 4, $"settings-about-{language}");

                window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).Should().Contain(about.VersionText);
                window.GetVisualDescendants().OfType<HyperlinkButton>().Select(l => l.NavigateUri).Should().Equal(
                    about.HomePage, about.IssuesPage, about.LicensePage, about.ThirdPartyNoticesPage, about.PrivacyPage, about.TermsPage);
                window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == openLogs)
                    .Command.Should().BeSameAs(about.OpenLogFolderCommand);
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the "About" page offers the newer version that was found.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="show">The expected caption of the button opening the update window.</param>
    [Theory]
    [InlineData("zh-Hans", "查看新版本")]
    [InlineData("en", "View new version")]
    public async Task SettingsWindow_AboutPageWithUpdate_ShowsUpdateButtons(string language, string show)
    {
        await Session.Dispatch(
            () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var settings = new FakeAppSettings();
                var updates = new FakeUpdateService { Status = UpdateStatus.Available, Available = FakeUpdateService.Newer };
                var about = new AboutViewModel(
                    new FakeDialogService(), LocalizationManager.Instance, new Swiftlet.Desktop.Hosting.AppPaths("data"),
                    updates: updates, settings: settings);
                var viewModel = new SettingsViewModel(
                    settings, LocalizationManager.Instance, new FakeRemoteImageRules(), about: about);
                var window = new SettingsWindow { DataContext = viewModel };
                Render(window, $"settings-update-window-{language}", language);

                SelectSettingsPage(window, 4, $"settings-about-update-{language}");

                window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == show)
                    .IsEffectivelyVisible.Should().BeTrue();
                window.Close();
                viewModel.Detach();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the update window shows the buttons that belong to each step.
    /// </summary>
    /// <param name="language">The UI language.</param>
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public async Task UpdateWindow_EachStep_ShowsItsButtons(string language)
    {
        await Session.Dispatch(
            () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var updates = new FakeUpdateService { Available = FakeUpdateService.Newer, CanInstall = true };
                var viewModel = new UpdateViewModel(updates, FakeUpdateService.Newer, "1.0.0", LocalizationManager.Instance);
                var window = new UpdateWindow { DataContext = viewModel };
                Render(window, $"update-prompt-{language}", language);

                bool Visible(string name) => window.FindControl<Button>(name)!.IsEffectivelyVisible;
                string[] names = ["SkipButton", "LaterButton", "CancelDownloadButton", "UpdateButton"];
                names.Select(Visible).Should().Equal(true, true, false, true);

                viewModel.Step = UpdateStep.Downloading;
                viewModel.ProgressText = "1 MB";
                viewModel.ProgressValue = 40;
                Snapshot(window, $"update-downloading-{language}");
                names.Select(Visible).Should().Equal(false, false, true, false);

                viewModel.ErrorText = LocalizationManager.Instance["Update_CloseWindowsFirst"];
                viewModel.Step = UpdateStep.Failed;
                Snapshot(window, $"update-failed-{language}");
                names.Select(Visible).Should().Equal(false, true, false, true);
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the "new version" pop-up renders and closes through its button.
    /// </summary>
    [Fact]
    public async Task UpdateNoticeWindow_Shown_ClosesThroughViewModel()
    {
        await Session.Dispatch(
            () =>
            {
                LocalizationManager.Instance.SetPreference("zh-Hans");
                var presenter = new AvaloniaUpdateNoticePresenter();
                var opened = 0;
                presenter.Show(new UpdateNoticeViewModel("2.0.0", LocalizationManager.Instance, () => opened++, presenter.Close));
                var window = presenter.Window!;
                Snapshot(window, "update-notice-zh-Hans");

                window.IsVisible.Should().BeTrue();
                var open = window.FindControl<Button>("OpenButton")!;
                open.Command!.Execute(null);

                opened.Should().Be(1);
                presenter.Window.Should().BeNull();
                window.IsVisible.Should().BeFalse();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds an AI page over fakes.
    /// </summary>
    /// <returns>The view model.</returns>
    private static AiSettingsViewModel CreateAiPage()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<Swiftlet.Application.Ai.HasAiKeyQuery>(_ => false);
        return new AiSettingsViewModel(
            new Swiftlet.Application.Ai.DisabledAiSettings(), dispatcher, new FakeDialogService(), LocalizationManager.Instance);
    }

    /// <summary>
    /// Verifies the sidebar has no "AI" and "About" entries when there is nothing to show on them.
    /// </summary>
    [Fact]
    public async Task SettingsWindow_NoAboutPage_HidesItsEntry()
    {
        await Session.Dispatch(
            () =>
            {
                var viewModel = new SettingsViewModel(new FakeAppSettings(), LocalizationManager.Instance, new FakeRemoteImageRules());
                var window = new SettingsWindow { DataContext = viewModel };
                Render(window, "settings-no-about", "en");

                window.GetVisualDescendants().OfType<TabItem>().Select(i => i.IsEffectivelyVisible)
                    .Should().Equal(true, true, true, false, false);
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Shows a page of the settings window and optionally saves a snapshot.
    /// </summary>
    /// <param name="window">The settings window.</param>
    /// <param name="index">The position of the page in the sidebar.</param>
    /// <param name="name">The snapshot file name without extension.</param>
    private static void SelectSettingsPage(Window window, int index, string name)
    {
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = index;
        Snapshot(window, name);
    }

    /// <summary>
    /// Verifies the AI buttons of the reading pane open a panel above the mail that shows the answer, and that they are
    /// absent without AI.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="summarize">The expected caption of the summary button.</param>
    /// <param name="title">The expected title of the panel.</param>
    [Theory]
    [InlineData("zh-Hans", "摘要", "邮件摘要")]
    [InlineData("en", "Summarize", "Summary")]
    public async Task MainWindow_AiSummary_ShowsPanelAboveTheMail(string language, string summarize, string title)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var scenario = new MainWindowViewModelTests.Scenario(withAi: true);
                scenario.Localizer.SetPreference(language);
                scenario.Dispatcher.OnStream<Swiftlet.Application.Ai.SummarizeMessageQuery, Swiftlet.Application.Ai.AiOutput>(
                    (_, token) => AiAssistViewModelTests.Parts(
                        [
                            new(Swiftlet.Application.Ai.AiOutputKind.InputTruncated),
                            new(Swiftlet.Application.Ai.AiOutputKind.Text, "The answer of the model.\n- one point\n- another"),
                        ],
                        token));
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, $"main-window-ai-closed-{language}", language);

                window.GetVisualDescendants().OfType<AiPanel>().Single().IsVisible.Should().BeFalse();
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => UiQueries.Caption(b) == summarize);
                button.IsEffectivelyVisible.Should().BeTrue();
                button.Command!.Execute(null);
                await scenario.ViewModel.AiAssist!.Running;
                Snapshot(window, $"main-window-ai-open-{language}");

                var panel = window.GetVisualDescendants().OfType<AiPanel>().Single();
                panel.IsEffectivelyVisible.Should().BeTrue();
                panel.GetVisualDescendants().OfType<TextBlock>().Select(b => b.Text).Should().Contain(title);
                panel.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text
                    .Should().StartWith("The answer of the model.");
                panel.GetVisualDescendants().OfType<RadioButton>().Select(b => b.IsChecked)
                    .Should().Equal(language == "zh-Hans", language == "en");
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the reading pane offers no AI actions without AI.
    /// </summary>
    [Fact]
    public async Task MainWindow_WithoutAi_ShowsNoAiActions()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-no-ai", "en");

                window.GetVisualDescendants().OfType<Button>()
                    .Where(b => UiQueries.Caption(b) is "Summarize" or "Translate")
                    .Should().HaveCount(2).And.OnlyContain(b => !b.IsEffectivelyVisible);
                window.GetVisualDescendants().OfType<AiPanel>().Single().IsEffectivelyVisible.Should().BeFalse();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the writing help of the compose window previews an answer and fits the narrowest window.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="writing">The expected caption of the writing help button.</param>
    /// <param name="minimum">Whether the window has its smallest size.</param>
    [Theory]
    [InlineData("zh-Hans", "写作辅助", false)]
    [InlineData("en", "Writing help", false)]
    [InlineData("en", "Writing help", true)]
    public async Task ComposeWindow_WritingHelp_PreviewsTheAnswer(string language, string writing, bool minimum)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var account = TestData.Account();
                var dispatcher = new FakeDispatcher();
                dispatcher.OnStream<Swiftlet.Application.Ai.RewriteTextQuery, Swiftlet.Application.Ai.AiOutput>(
                    (_, token) => AiAssistViewModelTests.Parts(
                        [new(Swiftlet.Application.Ai.AiOutputKind.Text, "A better wording of the text.")], token));
                var settings = new Swiftlet.Application.Ai.DisabledAiSettings();
                settings.SetAi(AiAssistViewModelTests.Ready());
                var request = new ComposeRequest(
                    ComposeMode.Reply, account.Id, "alice@example.com", "Re: Hello", "my text\n\n> quoted", null, "> quoted",
                    Guid.NewGuid());
                var viewModel = new ComposeViewModel(
                    dispatcher, new FakeDialogService(), LocalizationManager.Instance, [account], request, ai: settings);
                var window = new ComposeWindow { DataContext = viewModel };
                if (minimum)
                {
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                }

                var suffix = minimum ? $"{language}-minimum" : language;
                Render(window, $"compose-ai-closed-{suffix}", language);
                var panel = window.GetVisualDescendants().OfType<ComposeAiPanel>().Single();
                panel.IsVisible.Should().BeFalse();

                var toggle = window.GetVisualDescendants().OfType<Button>().Single(b => UiQueries.Caption(b) == writing);
                toggle.Command!.Execute(null);
                viewModel.Assist!.RewriteCommand.Execute(Swiftlet.Application.Ai.AiWritingAction.Polish);
                await viewModel.Assist.Running;
                Snapshot(window, $"compose-ai-open-{suffix}");

                panel.IsEffectivelyVisible.Should().BeTrue();
                panel.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text
                    .Should().Be("A better wording of the text.");
                panel.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
                    .Should().HaveCount(11, "the draft button, seven rewrites and what to do with the answer");
                var send = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == viewModel.SendCommand);
                send.TranslatePoint(new Point(0, send.Bounds.Height), window)!.Value.Y
                    .Should().BeLessThanOrEqualTo(window.Bounds.Height);
                var body = window.FindControl<TextBox>("BodyBox")!;
                body.Bounds.Height.Should().BeGreaterThan(30);
                body.TranslatePoint(new Point(0, body.Bounds.Height), window)!.Value.Y
                    .Should().BeLessThanOrEqualTo(panel.TranslatePoint(new Point(0, 0), window)!.Value.Y, "the panel covers no text");

                toggle.Command!.Execute(null);
                window.MinHeight.Should().Be(420);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the toolbar label flyout lists the labels and ticking one sends the set-label command.
    /// </summary>
    [Fact]
    public async Task MainWindow_LabelsFlyout_TickingLabelSendsCommand()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-labels-flyout", "en");

                var button = window.GetVisualDescendants().OfType<Button>().Single(b => UiQueries.Caption(b) == "Labels");
                button.IsEnabled.Should().BeTrue();
                button.Flyout!.ShowAt(button);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var popupRoot = button.Flyout.IsOpen ? ((Flyout)button.Flyout).Content as Control : null;
                popupRoot.Should().NotBeNull();
                var check = popupRoot!.GetVisualDescendants().OfType<CheckBox>().Single();
                check.IsChecked = true;
                check.Command!.Execute(check.CommandParameter);
                await scenario.ViewModel.WhenIdleAsync();

                scenario.Dispatcher.Sent<Swiftlet.Application.Labels.SetMessageLabelCommand>().Should().ContainSingle().Which.Assigned.Should().BeTrue();
                button.Flyout.Hide();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies account nodes carry a context menu whose commands reach the window view model, offering re-sign-in for OAuth accounts only.
    /// </summary>
    [Fact]
    public async Task MainWindow_AccountNodeContextMenu_OffersSettingsDeleteAndOAuthOnlyReauthenticate()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                var oauth = TestData.OAuthAccount();
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetAccountsQuery>(_ => new List<Swiftlet.Domain.Accounts.MailAccount> { scenario.Account, oauth });
                scenario.Dispatcher.On<Swiftlet.Application.Accounts.ReauthenticateOAuthAccountCommand>(_ => Mediator.Unit.Value);
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-account-menu", "en");

                var passwordMenu = OpenAccountMenu(window, scenario.ViewModel.Accounts[0]);
                passwordMenu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header).Should().Equal("Account settings…", "Remove account");
                passwordMenu.Close();
                var oauthMenu = OpenAccountMenu(window, scenario.ViewModel.Accounts[1]);
                var items = oauthMenu.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
                items.Select(i => i.Header).Should().Equal("Account settings…", "Remove account", "Re-sign in");
                items.Should().OnlyContain(i => i.Command != null && i.CommandParameter != null);
                items[2].Command!.Execute(items[2].CommandParameter);
                await scenario.ViewModel.WhenIdleAsync();

                scenario.Dispatcher.Sent<Swiftlet.Application.Accounts.ReauthenticateOAuthAccountCommand>()
                    .Should().ContainSingle().Which.AccountId.Should().Be(oauth.Id);
                oauthMenu.Close();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the message list allows selecting several messages, which reach the view model, and that Delete removes them.
    /// </summary>
    [Fact]
    public async Task MainWindow_MessageList_MultiSelectionAndDeleteKeyDeleteAll()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                scenario.Dialogs.ConfirmResult = true;
                scenario.Dispatcher.On<Swiftlet.Application.Messages.DeleteMessagesCommand>(c =>
                {
                    scenario.Messages.Clear();
                    return c.Messages.Count;
                });
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-multi-select", "en");
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();

                list.SelectAll();
                Snapshot(window, "main-window-multi-select-all");
                scenario.ViewModel.SelectedMessages.Should().HaveCount(2);
                var first = list.GetVisualDescendants().OfType<ListBoxItem>().First();
                first.Focus();
                window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                await scenario.ViewModel.WhenIdleAsync();

                scenario.Dispatcher.Sent<Swiftlet.Application.Messages.DeleteMessagesCommand>()
                    .Should().ContainSingle().Which.Messages.Should().HaveCount(2);
                scenario.ViewModel.Messages.Should().BeEmpty();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies right-clicking one of several selected messages keeps the selection for the context menu.
    /// </summary>
    [Fact]
    public async Task MainWindow_MessageList_RightClickKeepsMultiSelection()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-right-click", "en");
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                list.SelectAll();
                Dispatcher.UIThread.RunJobs();
                var second = list.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1);
                var point = second.TranslatePoint(new Point(10, 10), window)!.Value;

                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();

                scenario.ViewModel.SelectedMessages.Should().HaveCount(2);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the message context menu lists the target folders, keeping underscores, and a click moves the selection.
    /// </summary>
    [Fact]
    public async Task MainWindow_MessageContextMenu_MovesSelectionToChosenFolder()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                var custom = TestData.Folder(scenario.Account, "my_work", Swiftlet.Domain.Folders.FolderRole.Other);
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetFoldersQuery>(
                    _ => new List<Swiftlet.Domain.Folders.MailFolder> { scenario.Inbox, custom });
                scenario.Dispatcher.On<Swiftlet.Application.Messages.MoveMessagesCommand>(c =>
                {
                    scenario.Messages.Clear();
                    return c.MessageIds.Count;
                });
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-move-menu", "en");
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                list.SelectAll();
                Dispatcher.UIThread.RunJobs();

                // Opened with the mouse, as a user does, so the targets are filled while opening.
                var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
                var rowPoint = row.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseDown(rowPoint, MouseButton.Right);
                window.MouseUp(rowPoint, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
                var menu = list.ContextMenu!;
                menu.IsOpen.Should().BeTrue();
                Dispatcher.UIThread.RunJobs();
                var move = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Move to"));
                move.IsEffectivelyEnabled.Should().BeTrue();
                move.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
                Snapshot(window, "main-window-move-menu-open");
                var entry = (MenuItem)move.ContainerFromIndex(0)!;
                entry.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("my_work");
                var point = entry.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await scenario.ViewModel.WhenIdleAsync();

                var sent = scenario.Dispatcher.Sent<Swiftlet.Application.Messages.MoveMessagesCommand>().Should().ContainSingle().Which;
                sent.TargetFolderId.Should().Be(custom.Id);
                sent.MessageIds.Should().HaveCount(2);
                scenario.ViewModel.Messages.Should().BeEmpty();
                menu.IsOpen.Should().BeFalse();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the message context menu and the reading pane offer archiving, and the menu entry archives the selection.
    /// </summary>
    [Fact]
    public async Task MainWindow_MessageContextMenu_ArchivesSelection()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                var archive = TestData.Folder(scenario.Account, "Archive", Swiftlet.Domain.Folders.FolderRole.Archive);
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetFoldersQuery>(
                    _ => new List<Swiftlet.Domain.Folders.MailFolder> { scenario.Inbox, archive });
                scenario.Dispatcher.On<Swiftlet.Application.Messages.MoveMessagesCommand>(c =>
                {
                    scenario.Messages.Clear();
                    return c.MessageIds.Count;
                });
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-archive", "en");
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                list.SelectAll();
                Dispatcher.UIThread.RunJobs();
                Snapshot(window, "main-window-archive-selected");
                window.GetVisualDescendants().OfType<Button>()
                    .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Archive")
                    .IsEffectivelyEnabled.Should().BeTrue();

                var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
                var rowPoint = row.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseDown(rowPoint, MouseButton.Right);
                window.MouseUp(rowPoint, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
                var entry = list.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Archive"));
                Snapshot(window, "main-window-archive-menu-open");
                entry.IsEffectivelyEnabled.Should().BeTrue();
                var point = entry.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await scenario.ViewModel.WhenIdleAsync();

                var sent = scenario.Dispatcher.Sent<Swiftlet.Application.Messages.MoveMessagesCommand>().Should().ContainSingle().Which;
                sent.TargetFolderId.Should().Be(archive.Id);
                sent.MessageIds.Should().HaveCount(2);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies folder nodes offer deleting all messages, except for All Mail.
    /// </summary>
    [Fact]
    public async Task MainWindow_FolderNodeContextMenu_OffersDeleteAllExceptForAllMail()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                var allMail = TestData.Folder(scenario.Account, "All", Swiftlet.Domain.Folders.FolderRole.AllMail);
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetFoldersQuery>(
                    _ => new List<Swiftlet.Domain.Folders.MailFolder> { scenario.Inbox, allMail });
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-folder-menu", "en");

                var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
                foreach (var folder in scenario.ViewModel.Accounts[0].Folders)
                {
                    var row = UiQueries.MenuOwner(tree, folder);
                    var menu = row.ContextMenu!;
                    menu.Open(row);
                    Dispatcher.UIThread.RunJobs();
                    var item = menu.Items.OfType<MenuItem>().Should().ContainSingle().Which;
                    item.Header.Should().Be("Delete all messages");
                    item.IsEffectivelyEnabled.Should().Be(folder.CanEmpty);
                    item.CommandParameter.Should().BeSameAs(folder);
                    menu.Close();
                }

                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the sidebar offers composing, adding an account and the settings, and the language picker left the window.
    /// </summary>
    [Fact]
    public async Task MainWindow_Sidebar_HoldsTheGlobalActions()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-sidebar", "en");

                var sidebar = window.FindControl<Control>("Sidebar")!;
                UiQueries.ButtonCaptions(sidebar).Should().Contain(["Compose", "Add account", "Settings"]);
                UiQueries.ButtonCaptions(window.FindControl<Control>("MessagePane")!).Should().Contain("Sync");
                UiQueries.ButtonCaptions(window.FindControl<Control>("ReaderActions")!)
                    .Should().Contain(["Reply", "Forward", "Star", "Labels", "View in browser", "Delete"]);
                window.GetVisualDescendants().OfType<ComboBox>().Should().BeEmpty();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the message actions only show while a message is open.
    /// </summary>
    [Fact]
    public async Task MainWindow_NothingSelected_HidesReaderActions()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-no-actions");
                var actions = window.FindControl<Control>("ReaderActions")!;
                actions.IsEffectivelyVisible.Should().BeFalse();

                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                Snapshot(window, "main-window-actions");

                actions.IsEffectivelyVisible.Should().BeTrue();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the three panes and every message action stay inside the narrowest window, even with a very long address.
    /// </summary>
    /// <param name="language">The UI language.</param>
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public async Task MainWindow_MinimumWidth_PanesAndActionsFit(string language)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                // With AI, the pane has the most actions.
                var scenario = new MainWindowViewModelTests.Scenario(withAi: true);
                scenario.Localizer.SetPreference(language);
                var longAccount = TestData.Account($"{new string('a', 68)}@example.com");
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetAccountsQuery>(
                    _ => new List<Swiftlet.Domain.Accounts.MailAccount> { scenario.Account, longAccount });
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();

                var window = new MainWindow { DataContext = scenario.ViewModel, Width = 800, Height = 480 };
                Render(window, $"main-window-narrow-{language}", language);

                window.FindControl<Control>("Sidebar")!.Bounds.Width.Should().BeGreaterThanOrEqualTo(180);
                window.FindControl<Control>("MessagePane")!.Bounds.Width.Should().BeGreaterThanOrEqualTo(260);
                var reader = window.FindControl<Control>("ReaderPane")!;
                reader.Bounds.Width.Should().BeGreaterThanOrEqualTo(300);
                reader.Bounds.Right.Should().BeLessThanOrEqualTo(800);
                var buttons = window.FindControl<Control>("ReaderActions")!.GetVisualDescendants().OfType<Button>().ToList();
                buttons.Should().HaveCount(9);
                foreach (var button in buttons)
                {
                    button.Bounds.Width.Should().BeGreaterThan(0);
                    button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X
                        .Should().BeLessThanOrEqualTo(800, UiQueries.Caption(button));
                }

                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies selecting an account replaces the message list and reader with the account page, which fills a wide window
    /// with two columns and fits the narrowest window with one, in both themes.
    /// </summary>
    /// <param name="language">The UI language.</param>
    /// <param name="sync">The caption of the sync button.</param>
    /// <param name="dark">Whether the dark theme is used.</param>
    [Theory]
    [InlineData("zh-Hans", "立即同步", false)]
    [InlineData("en", "Sync now", false)]
    [InlineData("zh-Hans", "立即同步", true)]
    public async Task MainWindow_AccountSelected_ShowsAccountPage(string language, string sync, bool dark)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var scenario = new MainWindowViewModelTests.Scenario
                {
                    SyncStatus = new Swiftlet.Application.Sync.AccountSyncStatus(
                        false, DateTimeOffset.UnixEpoch, ["Projects"], DateTimeOffset.UnixEpoch.AddDays(1), "The server is not reachable.", false),
                };
                scenario.Localizer.SetPreference(language);
                await scenario.StartAsync();
                var application = Avalonia.Application.Current!;
                application.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                try
                {
                    var window = new MainWindow { DataContext = scenario.ViewModel };
                    Render(window, "main-window-account-before", language);
                    scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0];
                    await scenario.ViewModel.WhenIdleAsync();
                    var suffix = $"{language}-{(dark ? "dark" : "light")}";
                    Snapshot(window, $"main-window-account-{suffix}");

                    window.FindControl<Control>("MessagePane")!.IsVisible.Should().BeFalse();
                    window.FindControl<Control>("ReaderPane")!.IsVisible.Should().BeFalse();
                    var pane = window.FindControl<Control>("AccountPane")!;
                    pane.IsVisible.Should().BeTrue();
                    var captions = pane.GetVisualDescendants().OfType<Button>()
                        .Where(b => b.IsEffectivelyVisible)
                        .Select(UiQueries.Caption)
                        .ToList();
                    captions.Should().Contain(sync).And.HaveCount(3, "the password account cannot sign in again in the browser");

                    window.Width = 1920;
                    window.Height = 900;
                    Snapshot(window, $"main-window-account-wide-{suffix}");
                    var page = pane.GetVisualDescendants().OfType<Swiftlet.Desktop.Views.AccountPage>().Single();
                    var content = page.FindControl<Control>("PageContent")!;
                    var actions = page.FindControl<Control>("HeaderActions")!;
                    var statistics = page.FindControl<Control>("StatisticsCard")!;
                    var settings = page.FindControl<Control>("SettingsColumn")!;
                    content.Bounds.Width.Should().BeApproximately(
                        page.Bounds.Width - content.Margin.Left - content.Margin.Right, 2, "the page fills the pane");
                    settings.Bounds.Top.Should().Be(statistics.Bounds.Top, "a wide page has two columns");
                    settings.Bounds.Left.Should().BeGreaterThan(statistics.Bounds.Right);
                    settings.Bounds.Right.Should().BeApproximately(content.Bounds.Width, 2);
                    Grid.GetRow(actions).Should().Be(0, "the buttons stand beside the name");

                    window.Width = 800;
                    window.Height = 480;
                    Snapshot(window, $"main-window-account-narrow-{suffix}");
                    settings.Bounds.Top.Should().BeGreaterThan(statistics.Bounds.Bottom, "a narrow page has one column");
                    statistics.Bounds.Width.Should().BeApproximately(content.Bounds.Width, 2);
                    settings.Bounds.Width.Should().BeApproximately(content.Bounds.Width, 2);
                    Grid.GetRow(actions).Should().Be(1, "the buttons move below the name");
                    foreach (var button in pane.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
                    {
                        button.Bounds.Width.Should().BeGreaterThan(0);
                        button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X
                            .Should().BeLessThanOrEqualTo(800, UiQueries.Caption(button));
                    }

                    window.Close();
                }
                finally
                {
                    application.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
                }
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the search conditions are hidden until the filter button is pressed, stay inside the narrowest window and
    /// are bound to the view model.
    /// </summary>
    /// <param name="language">The UI language.</param>
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public async Task MainWindow_SearchFilters_ExpandOnDemandAndBind(string language)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference(language);
                await scenario.StartInInboxAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel, Width = 800, Height = 600 };
                Render(window, $"main-window-search-collapsed-{language}", language);
                var panel = window.FindControl<Control>("SearchFilters")!;
                var toggle = window.FindControl<ToggleButton>("FilterToggle")!;
                panel.IsVisible.Should().BeFalse();

                toggle.IsChecked = true;
                var filter = scenario.ViewModel.Filter;
                filter.From = "alice";
                filter.DateFrom = new DateOnly(2026, 3, 2);
                filter.DateTo = new DateOnly(2026, 3, 1);
                Render(window, $"main-window-search-filters-{language}", language);

                panel.IsVisible.Should().BeTrue();
                window.FindControl<Control>("FilterIndicator")!.IsVisible.Should().BeTrue();
                var pickers = panel.GetVisualDescendants().OfType<CalendarDatePicker>().ToList();
                pickers.Select(p => p.SelectedDate).Should().Equal(new DateTime(2026, 3, 2), new DateTime(2026, 3, 1));
                var pane = window.FindControl<Control>("MessagePane")!;
                var inputs = panel.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is TextBox { TemplatedParent: null } or CalendarDatePicker or ComboBox or CheckBox or Button { TemplatedParent: null });
                foreach (var input in inputs)
                {
                    input.TranslatePoint(new Point(input.Bounds.Width, 0), pane)!.Value.X.Should().BeLessThanOrEqualTo(pane.Bounds.Width);
                }

                pickers[1].SelectedDate = new DateTime(2026, 3, 5);
                filter.DateTo.Should().Be(new DateOnly(2026, 3, 5));
                var scope = panel.GetVisualDescendants().OfType<ComboBox>().Single();
                scope.SelectedIndex.Should().Be((int)SearchScope.CurrentAccount);
                scope.SelectedIndex = (int)SearchScope.CurrentFolder;
                filter.ScopeIndex.Should().Be((int)SearchScope.CurrentFolder);

                toggle.IsChecked = false;
                Render(window, $"main-window-search-hidden-{language}", language);
                panel.IsVisible.Should().BeFalse();
                filter.From.Should().Be("alice");
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies every button showing only an icon says what it does, both in a tooltip and to assistive technology.
    /// </summary>
    [Fact]
    public async Task MainWindow_IconButtons_HaveNameAndToolTip()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-icon-buttons");

                var iconButtons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Content is PathIcon).ToList();

                iconButtons.Should().HaveCountGreaterThanOrEqualTo(6);
                iconButtons.Should().OnlyContain(b =>
                    !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))
                    && !string.IsNullOrEmpty(ToolTip.GetTip(b) as string));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the selected folder is marked with the subtle accent and a disabled icon button gets no grey box.
    /// </summary>
    [Fact]
    public async Task MainWindow_SelectionAndDisabledButtons_UseTheDesignColors()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-selection-colors");

                var row = window.GetVisualDescendants().OfType<TreeViewItem>().Single(i => i.IsSelected);
                var layout = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
                Color(layout.Background).Should().Be(Token(window, "Brush.AccentSubtle"));
                var disabled = window.FindControl<Control>("ReaderActions")!.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible && !b.IsEffectivelyEnabled)
                    .ToList();
                disabled.Should().NotBeEmpty();
                foreach (var button in disabled)
                {
                    var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
                    Color(presenter.Background).A.Should().Be(0);
                }

                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a row shows the unread dot only while unread, the short time, and label chips tinted with the label color.
    /// </summary>
    [Fact]
    public async Task MainWindow_MessageRow_ShowsUnreadDotListTimeAndChips()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Messages[0].AddLabel(scenario.Work);
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-rows");

                var rows = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();
                rows.Should().HaveCount(2);
                for (var i = 0; i < rows.Count; i++)
                {
                    var item = scenario.ViewModel.Messages[i];
                    var dot = rows[i].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("unreadDot"));
                    dot.IsEffectivelyVisible.Should().Be(item.IsUnread);
                    rows[i].GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain(item.ListTimeText);
                }

                scenario.ViewModel.Messages.Select(m => m.IsUnread).Should().Contain(true).And.Contain(false);
                var chip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("chip"));
                Color(chip.Background).Should().Be(Avalonia.Media.Color.Parse("#26E53935"));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the selected row has the subtle accent background and an accent bar, and the list has no grey fill.
    /// </summary>
    [Fact]
    public async Task MainWindow_SelectedMessageRow_UsesSubtleAccentAndBar()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-selected-row");

                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                Color(list.Background).A.Should().Be(0);
                var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected);
                var presenter = row.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .First(c => c.Name == "PART_ContentPresenter");
                Color(presenter.Background).Should().Be(Token(window, "Brush.AccentSubtle"));
                Color(presenter.BorderBrush).Should().Be(Token(window, "Brush.Accent"));
                presenter.BorderThickness.Left.Should().Be(3);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a window without accounts shows the notice with a button that adds one.
    /// </summary>
    [Fact]
    public async Task MainWindow_NoAccounts_ShowsAddAccountNotice()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario(hasAccount: false);
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-no-accounts", "en");

                var pane = window.FindControl<Control>("MessagePane")!;
                pane.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)
                    .Should().Contain("Add a mail account to get started").And.NotContain("No messages here");
                var button = pane.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.IsEffectivelyVisible && UiQueries.Caption(b) == "Add account");
                button.Command.Should().BeSameAs(scenario.ViewModel.AddAccountCommand);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a very long subject and attachment name stay inside the reading pane of the narrowest window, and the
    /// sender avatar shows.
    /// </summary>
    [Fact]
    public async Task MainWindow_LongTextsAt800_StayInsideReader()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, new string('W', 160));
                scenario.Messages[1].AddAttachment($"{new string('f', 120)}.pdf", "application/pdf", 10);
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel, Width = 800, Height = 480 };
                Render(window, "main-window-long-texts");

                var reader = window.FindControl<Control>("ReaderPane")!;
                var visible = reader.GetVisualDescendants().OfType<Control>()
                    .Where(c => c is TextBlock or Button && c.IsEffectivelyVisible).ToList();
                visible.OfType<TextBlock>().Select(t => t.Text).Should().Contain("A");
                var body = reader.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                body.GetVisualAncestors().OfType<ScrollViewer>().First().Bounds.Height
                    .Should().BeGreaterThan(60, "a long subject must not push the text out of sight");
                foreach (var control in visible)
                {
                    control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X
                        .Should().BeLessThanOrEqualTo(800.5, (control as TextBlock)?.Text ?? control.ToString());
                }

                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies every address of the header is a named button with a menu, many recipients stay inside the narrowest
    /// window, and they do not push the text out of sight.
    /// </summary>
    [Fact]
    public async Task MainWindow_ManyRecipientsAt800_ShowsAddressButtonsWithMenu()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                scenario.ContactNames["alice@example.com"] = "Alice Smith";
                var recipients = string.Join(", ", Enumerable.Range(0, 40).Select(i => $"person_{i}@example.com"))
                    + $", {new string('r', 120)}@example.com";
                scenario.Messages[1] = Swiftlet.Domain.Messages.MailMessage.Create(
                    scenario.Account.Id,
                    scenario.Inbox.Id,
                    2,
                    "To everyone",
                    "Alice",
                    "alice@example.com",
                    recipients,
                    new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
                    "text",
                    null,
                    "<2@example.com>",
                    true);
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel, Width = 800, Height = 480 };
                Render(window, "main-window-recipients", "en");

                var reader = window.FindControl<Control>("ReaderPane")!;
                var buttons = reader.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("address")).ToList();
                buttons.Should().HaveCount(42);
                UiQueries.ButtonCaptions(window).Should().Contain("Alice Smith <alice@example.com>");
                foreach (var button in buttons)
                {
                    button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(800.5);
                }

                var body = reader.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                body.GetVisualAncestors().OfType<ScrollViewer>().First().Bounds.Height.Should().BeGreaterThan(60);

                var sender = buttons.Single(b => ReferenceEquals(b.DataContext, scenario.ViewModel.SelectedMessage!.SenderAddress));
                var point = sender.TranslatePoint(new Point(10, 8), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                var flyout = (MenuFlyout)sender.Flyout!;
                flyout.IsOpen.Should().BeTrue();
                Snapshot(window, "main-window-address-menu");
                var items = flyout.Items.OfType<MenuItem>().ToList();
                items.Select(i => i.Header).Should().Equal("View contact", "Write email", "Copy address");
                items[2].Command!.Execute(null);
                scenario.Dialogs.CopiedTexts.Should().Equal("alice@example.com");
                flyout.Hide();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the message context menu offers the sender and recipients for the address book.
    /// </summary>
    [Fact]
    public async Task MainWindow_MessageContextMenu_ListsContactTargets()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-contact-menu", "en");
                var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemCount == 2);

                var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
                var rowPoint = row.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseDown(rowPoint, MouseButton.Right);
                window.MouseUp(rowPoint, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
                var menu = list.ContextMenu!;
                menu.IsOpen.Should().BeTrue();
                Dispatcher.UIThread.RunJobs();
                var add = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Add to contacts"));
                add.IsEffectivelyEnabled.Should().BeTrue();
                add.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
                Snapshot(window, "main-window-contact-menu-open");
                var entry = (MenuItem)add.ContainerFromIndex(0)!;
                entry.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                    .Should().Contain("From: Alice <alice@example.com>");

                entry.Command!.Execute(null);

                scenario.Dialogs.ContactsRequests.Should().ContainSingle()
                    .Which!.Address.Should().Be("alice@example.com");
                menu.Close();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the attachment preview window shows the file, then a thumbnail or text, and replaces the loading message.
    /// </summary>
    [Fact]
    public async Task AttachmentPreviewWindow_LoadedContent_ShowsTextAndThumbnail()
    {
        await DispatchAsync(
            () =>
            {
                var account = TestData.Account();
                var message = TestData.Message(account, TestData.Folder(account, "INBOX", Swiftlet.Domain.Folders.FolderRole.Inbox), 1, "Hello");
                message.AddAttachment("notes.txt", "text/plain", 2048);
                var attachment = new Swiftlet.Desktop.ViewModels.Main.AttachmentItemViewModel(
                    account.Id, message.Attachments[0], _ => Task.CompletedTask);
                var viewModel = new Swiftlet.Desktop.ViewModels.Main.AttachmentPreviewViewModel(attachment, TestLocalizer.Chinese());
                var window = new AttachmentPreviewWindow { DataContext = viewModel };
                Render(window, "attachment-preview-loading");
                window.FindControl<TextBlock>("PreviewMessage")!.IsVisible.Should().BeTrue();

                viewModel.Apply(new Swiftlet.Application.Abstractions.AttachmentPreview(
                    Swiftlet.Application.Abstractions.AttachmentPreviewKind.Text, Text: "第一行\nsecond line", Truncated: true));
                Snapshot(window, "attachment-preview-text");

                window.FindControl<TextBlock>("PreviewMessage")!.IsVisible.Should().BeFalse();
                var text = window.FindControl<TextBlock>("PreviewText")!;
                text.IsVisible.Should().BeTrue();
                text.Text.Should().Contain("second line");
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies each attachment of a new message has a named remove button, and the address rows are borderless.
    /// </summary>
    [Fact]
    public async Task ComposeWindow_WithAttachment_ShowsNamedRemoveButton()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var account = TestData.Account();
                var dialogs = new FakeDialogService();
                dialogs.PickedFiles.Add(Path.Combine(Path.GetTempPath(), "swiftlet-missing-notes.txt"));
                var viewModel = new ComposeViewModel(
                    new FakeDispatcher(),
                    dialogs,
                    TestLocalizer.English(),
                    [account],
                    new ComposeRequest(ComposeMode.New, account.Id, string.Empty, string.Empty, string.Empty, null));
                await viewModel.AddAttachmentsCommand.ExecuteAsync(null);
                var window = new ComposeWindow { DataContext = viewModel };
                Render(window, "compose-attachment", "en");

                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("swiftlet-missing-notes.txt");
                var remove = window.GetVisualDescendants().OfType<Button>()
                    .Single(b => UiQueries.Caption(b) == "Remove attachment");
                ToolTip.GetTip(remove).Should().Be("Remove attachment");
                window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("flat")).Should().HaveCount(2);
                var recipientBoxes = window.GetVisualDescendants().OfType<RecipientBox>().ToList();
                recipientBoxes.Should().HaveCount(3).And.OnlyContain(b => b.Classes.Contains("flat"));
                recipientBoxes[0].GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement")
                    .BorderThickness.Should().Be(new Thickness(0));
                var border = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("flat"))
                    .GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                border.BorderThickness.Should().Be(new Thickness(0));

                remove.Command!.Execute(null);

                viewModel.Attachments.Should().BeEmpty();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Creates a loaded contacts view model with three contacts and two collected addresses.
    /// </summary>
    /// <param name="localizer">The localizer.</param>
    /// <returns>The view model.</returns>
    private static async Task<ContactsViewModel> CreateContactsAsync(ILocalizer localizer)
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<Swiftlet.Application.Contacts.GetAddressBookQuery>(_ => new Swiftlet.Application.Contacts.AddressBook(
            [
                Swiftlet.Domain.Contacts.Contact.Create(
                    "三", "张", ["zhang@example.com", "zhangsan@work.example.com"], ["+86 138 0000 0000"], "Acme", "同事"),
                Swiftlet.Domain.Contacts.Contact.Create("Alice", "Walker", ["alice@example.com"]),
                Swiftlet.Domain.Contacts.Contact.Create("Ben", "Ortiz", ["ben@example.com"], company: "Northwind"),
            ],
            [
                new("bob@example.com", "Bob", DateTimeOffset.UnixEpoch),
                new("newsletter@example.org", string.Empty, DateTimeOffset.UnixEpoch),
            ]));
        var viewModel = new ContactsViewModel(dispatcher, new FakeDialogService(), localizer);
        await viewModel.InitializeAsync();
        return viewModel;
    }

    /// <summary>
    /// Verifies the confirmation window answers Enter and Escape with Cancel and marks the confirming button as destructive.
    /// </summary>
    [Fact]
    public async Task ConfirmWindow_Create_CancelIsDefaultAndOkIsDanger()
    {
        await DispatchAsync(
            () =>
            {
                var window = ConfirmWindow.Create("Delete", "Delete everything?", "Delete", "Cancel");
                Render(window, "confirm", "en");

                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                var ok = buttons.Single(b => UiQueries.Caption(b) == "Delete");
                var cancel = buttons.Single(b => UiQueries.Caption(b) == "Cancel");
                ok.Classes.Should().Contain("danger");
                ok.IsDefault.Should().BeFalse();
                cancel.IsDefault.Should().BeTrue();
                cancel.IsCancel.Should().BeTrue();
                window.Title.Should().Be("Delete");
                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Delete everything?");
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Renders every window in the dark theme, where each takes the dark background, and checks its icon-only buttons
    /// are named.
    /// </summary>
    [Fact]
    public async Task AllWindows_DarkTheme_UseDarkBackgroundAndNameIconButtons()
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("zh-Hans");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Messages[1].AddAttachment("quarterly-report.pdf", "application/pdf", 1_250_000);
                scenario.Messages[0].AddLabel(scenario.Work);
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                await scenario.ViewModel.WhenIdleAsync();
                var localizer = TestLocalizer.Chinese();
                var account = TestData.Account();
                var dialogs = new FakeDialogService();
                dialogs.PickedFiles.Add("notes.txt");
                var compose = new ComposeViewModel(
                    new FakeDispatcher(), dialogs, localizer, [account],
                    new ComposeRequest(ComposeMode.Reply, account.Id, "alice@example.com", "Re: Hello", "> quoted", null));
                await compose.AddAttachmentsCommand.ExecuteAsync(null);
                var dispatcher = new FakeDispatcher();
                dispatcher.On<Swiftlet.Application.Labels.GetLabelsQuery>(_ =>
                    (IReadOnlyList<Swiftlet.Domain.Labels.Label>)new List<Swiftlet.Domain.Labels.Label> { scenario.Work });
                var labels = new LabelManagerViewModel(dispatcher, LocalizationManager.Instance);
                await labels.InitializeAsync();
                var protection = new FakeDatabaseProtection();
                protection.Results.Enqueue(new Swiftlet.Infrastructure.Persistence.Encryption.DatabaseProtectionResult(
                    Swiftlet.Infrastructure.Persistence.Encryption.DatabaseProtectionProblem.KeyLost));
                var startup = new StartupViewModel(
                    protection, localizer, new FakeDialogService(), new FakeUiThread(), _ => Task.FromResult<string?>(null));
                await startup.RunCommand.ExecuteAsync(null);
                var editAccount = new EditAccountViewModel(new FakeDispatcher(), localizer);
                editAccount.Load(account, []);
                scenario.ViewModel.Filter.IsExpanded = true;
                var contacts = await CreateContactsAsync(localizer);
                var application = Avalonia.Application.Current!;
                application.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                try
                {
                    (string Name, Window Window)[] windows =
                    [
                        ("main", new MainWindow { DataContext = scenario.ViewModel }),
                        ("compose", new ComposeWindow { DataContext = compose }),
                        ("add-account", new AddAccountWindow { DataContext = new AddAccountViewModel(new FakeDispatcher(), localizer) { Email = "me@corp.example", Password = "x" } }),
                        ("edit-account", new EditAccountWindow { DataContext = editAccount }),
                        ("settings", new SettingsWindow { DataContext = new SettingsViewModel(new FakeAppSettings(), localizer, new FakeRemoteImageRules()) }),
                        ("labels", new LabelManagerWindow { DataContext = labels }),
                        ("contacts", new ContactsWindow { DataContext = contacts }),
                        ("startup", new StartupWindow { DataContext = startup }),
                        ("confirm", ConfirmWindow.Create("删除", "确定删除这封邮件吗？", "删除", "取消")),
                        ("new-mail", new NewMailWindow
                        {
                            DataContext = new NewMailViewModel(
                                new Swiftlet.Application.Sync.NewMailEventArgs(
                                    account.Id, account.EmailAddress, [Summary("Alice", "Hello", DateTimeOffset.UnixEpoch)]),
                                QuickActionDispatcher(),
                                localizer,
                                () => { }),
                        }),
                    ];
                    foreach (var (name, window) in windows)
                    {
                        Render(window, $"dark-{name}");

                        // The pop-up is a transparent window around a card with round corners.
                        var background = window is NewMailWindow
                            ? window.FindControl<Border>("Card")!.Background
                            : window.Background;
                        Color(background).Should().Be(
                            Avalonia.Media.Color.Parse(window is NewMailWindow ? "#1B232B" : "#12171C"), name);
                        // Scroll bar buttons belong to control templates, so only plain buttons are checked.
                        window.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.Content is PathIcon)
                            .Should().OnlyContain(
                            b => !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))
                                && !string.IsNullOrEmpty(ToolTip.GetTip(b) as string),
                            name);
                        window.Close();
                    }

                    var about = new AboutViewModel(new FakeDialogService(), localizer, new Swiftlet.Desktop.Hosting.AppPaths("data"));
                    var settings = new SettingsWindow
                    {
                        DataContext = new SettingsViewModel(
                            new FakeAppSettings(), localizer, new FakeRemoteImageRules(), protection, about: about),
                    };
                    Render(settings, "dark-settings-general");
                    foreach (var (index, page) in ((int, string)[])[(1, "sync"), (2, "privacy"), (3, "about")])
                    {
                        SelectSettingsPage(settings, index, $"dark-settings-{page}");
                    }

                    settings.Close();
                }
                finally
                {
                    application.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
                }
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Describes an announced message.
    /// </summary>
    /// <param name="sender">The sender name.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="sentAt">When it was sent.</param>
    /// <returns>The summary.</returns>
    private static Swiftlet.Application.Sync.NewMailSummary Summary(string sender, string subject, DateTimeOffset sentAt)
    {
        return new Swiftlet.Application.Sync.NewMailSummary(
            sender,
            "sender@example.com",
            subject,
            sentAt,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Shall we meet at noon? I booked a table near the office, tell me whether that works for you.");
    }

    /// <summary>
    /// Creates a dispatcher answering that messages can be deleted but not archived.
    /// </summary>
    /// <returns>The dispatcher.</returns>
    private static FakeDispatcher QuickActionDispatcher()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<Swiftlet.Application.Messages.GetQuickActionsQuery>(
            _ => new Swiftlet.Application.Messages.QuickActionAvailability(true, false));
        return dispatcher;
    }

    /// <summary>
    /// Verifies the new mail pop-up shows its texts in both themes, and its buttons are named and work.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <param name="expectedTitle">The expected heading.</param>
    [Theory]
    [InlineData("en", "2 new messages")]
    [InlineData("zh-Hans", "2 封新邮件")]
    public async Task NewMailPopup_ShowsTextsAndClosesFromItsButton(string language, string expectedTitle)
    {
        await DispatchAsync(
            async () =>
            {
                LocalizationManager.Instance.SetPreference(language);
                var arrival = new Swiftlet.Application.Sync.NewMailEventArgs(
                    Guid.NewGuid(),
                    "me@example.com",
                    [
                        Summary("Alice", "A rather long subject that has to wrap onto a second line and then be cut off with an ellipsis", DateTimeOffset.UnixEpoch.AddDays(1)),
                        Summary("Bob", "Earlier", DateTimeOffset.UnixEpoch),
                    ]);
                using var viewModel = new NewMailViewModel(arrival, QuickActionDispatcher(), LocalizationManager.Instance, () => { });
                await viewModel.AvailabilityLoad;
                var presenter = new AvaloniaNewMailPresenter();
                var closeRequests = 0;
                viewModel.CloseRequested += (_, _) =>
                {
                    closeRequests++;
                    presenter.Close();
                };

                presenter.Show(viewModel);
                var window = presenter.Window!;
                Snapshot(window, $"new-mail-{language}");

                window.ShowActivated.Should().BeFalse();
                window.TransparencyLevelHint.Should().Equal(WindowTransparencyLevel.Transparent);
                Color(window.Background).A.Should().Be(0);
                window.FindControl<Border>("Card")!.CornerRadius.Should().Be(new CornerRadius(12));
                window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                    .Should().Contain([expectedTitle, "Alice"]);
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                buttons.Select(b => b.Name).Should().BeEquivalentTo(
                    "CloseButton", "OpenButton", "PreviousButton", "NextButton",
                    "StarButton", "MarkReadButton", "ArchiveButton", "DeleteButton");
                buttons.Should().OnlyContain(b => !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b)));
                buttons.Single(b => b.Name == "DeleteButton").IsEffectivelyEnabled.Should().BeTrue();
                var archive = buttons.Single(b => b.Name == "ArchiveButton");
                archive.IsEffectivelyEnabled.Should().BeFalse();
                ToolTip.GetShowOnDisabled(archive).Should().BeTrue();

                // The action row must not move when the shown message has shorter texts.
                var height = window.Bounds.Height;
                viewModel.NextCommand.Execute(null);
                await viewModel.AvailabilityLoad;
                Snapshot(window, $"new-mail-second-{language}");
                window.Bounds.Height.Should().Be(height);

                buttons.Single(b => b.Name == "CloseButton").Command!.Execute(null);

                closeRequests.Should().Be(1);
                presenter.Window.Should().BeNull();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a pop-up window closed from outside tells its view model.
    /// </summary>
    [Fact]
    public async Task NewMailPopup_ClosedByWindowSystem_RequestsClose()
    {
        await DispatchAsync(
            () =>
            {
                var arrival = new Swiftlet.Application.Sync.NewMailEventArgs(
                    Guid.NewGuid(), "me@example.com", [Summary("Alice", "Hello", DateTimeOffset.UnixEpoch)]);
                using var viewModel = new NewMailViewModel(arrival, QuickActionDispatcher(), LocalizationManager.Instance, () => { });
                var presenter = new AvaloniaNewMailPresenter();
                var closeRequests = 0;
                viewModel.CloseRequested += (_, _) => closeRequests++;
                presenter.Show(viewModel);

                presenter.Window!.Close();

                closeRequests.Should().Be(1);
                presenter.Window.Should().BeNull();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the tree shows the unread badge next to the inbox and its account, and none next to a folder without
    /// unread mail.
    /// </summary>
    [Fact]
    public async Task MainWindow_UnreadMessages_ShowBadgeInTree()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-unread");

                var badges = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("badge") && t.IsEffectivelyVisible).ToList();
                badges.Select(t => t.Text).Should().Equal("2", "2", "2");
                badges.Select(t => t.DataContext).Should().Contain(scenario.ViewModel.UnifiedInbox)
                    .And.Contain(scenario.ViewModel.Accounts[0]);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies rows with unread mail stand out by weight and badge color, and return to normal once read.
    /// </summary>
    [Fact]
    public async Task MainWindow_RowsWithUnreadMail_AreHighlighted()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-unread-highlight");
                var account = scenario.ViewModel.Accounts[0];
                var inbox = account.Folders.Single(f => f.Folder.Id == scenario.Inbox.Id);
                var sent = account.Folders.Single(f => f.Folder.Id == scenario.Sent.Id);
                object[] unreadRows = [scenario.ViewModel.UnifiedInbox, account, inbox];

                foreach (var row in unreadRows)
                {
                    RowName(window, row).FontWeight.Should().Be(Avalonia.Media.FontWeight.SemiBold);
                }

                RowName(window, sent).FontWeight.Should().Be(Avalonia.Media.FontWeight.Normal);
                foreach (var badge in VisibleBadges(window))
                {
                    badge.FontWeight.Should().Be(Avalonia.Media.FontWeight.SemiBold);
                    Color(badge.Foreground).Should().Be(Token(window, "Brush.AccentText"));
                }

                inbox.UnreadCount = 0;
                account.UnreadCount = 0;
                scenario.ViewModel.UnifiedInbox.UnreadCount = 0;
                Snapshot(window, "main-window-unread-read");

                foreach (var row in unreadRows)
                {
                    RowName(window, row).FontWeight.Should().Be(Avalonia.Media.FontWeight.Normal);
                }

                VisibleBadges(window).Should().BeEmpty();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies each unread badge sits right after its folder name, however wide the sidebar is.
    /// </summary>
    [Fact]
    public async Task MainWindow_UnreadBadges_FollowFolderName()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-unread-narrow");
                var sidebar = (Grid)window.FindControl<Border>("Sidebar")!.Parent!;

                foreach (var width in (double[])[220, 360])
                {
                    sidebar.ColumnDefinitions[0].Width = new GridLength(width);
                    Snapshot(window, $"main-window-unread-{width}");

                    var badges = VisibleBadges(window);
                    badges.Should().HaveCount(3);
                    foreach (var badge in badges)
                    {
                        var name = ((Panel)badge.Parent!).Children.OfType<TextBlock>().Single(t => t != badge);
                        (badge.Bounds.Left - name.Bounds.Right).Should().BeApproximately(8, 1, $"the sidebar is {width} wide");
                    }
                }

                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a long folder name is trimmed so that its unread badge stays visible, left of the scroll bar.
    /// </summary>
    [Fact]
    public async Task MainWindow_UnreadBadges_LongName_StayClearOfScrollBar()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                var folder = TestData.Folder(scenario.Account, new string('W', 80), Swiftlet.Domain.Folders.FolderRole.Other);
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetFoldersQuery>(
                    _ => new List<Swiftlet.Domain.Folders.MailFolder> { folder });
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetUnreadCountsQuery>(
                    _ => new Dictionary<Guid, int> { [folder.Id] = 123 });
                await scenario.StartAsync();
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-unread-long-name");

                var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
                var badge = VisibleBadges(window).Single(b => b.DataContext is FolderNodeViewModel);
                var right = badge.TranslatePoint(new Point(badge.Bounds.Width, 0), tree)!.Value.X;

                badge.Bounds.Width.Should().BeGreaterThan(0);
                right.Should().BeLessThanOrEqualTo(tree.Bounds.Width - 12);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the unread counts are read away from the UI thread, which database calls would otherwise block.
    /// </summary>
    [Fact]
    public async Task MainWindow_UnreadCounts_AreReadOffTheUiThread()
    {
        await DispatchAsync(
            async () =>
            {
                var scenario = new MainWindowViewModelTests.Scenario();
                var onUiThread = new List<bool>();
                scenario.Dispatcher.On<Swiftlet.Application.Queries.GetUnreadCountsQuery>(_ =>
                {
                    onUiThread.Add(Dispatcher.UIThread.CheckAccess());
                    return new Dictionary<Guid, int> { [scenario.Inbox.Id] = 7 };
                });

                await scenario.StartAsync();

                onUiThread.Should().NotBeEmpty().And.OnlyContain(v => !v);
                scenario.ViewModel.UnifiedInbox.UnreadCount.Should().Be(7);
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Finds the unread badges shown in a window.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <returns>The visible badges.</returns>
    private static List<TextBlock> VisibleBadges(Window window)
    {
        return window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("badge") && t.IsEffectivelyVisible).ToList();
    }

    /// <summary>
    /// Finds the name shown on a tree row that can carry an unread badge.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="row">The node of the row.</param>
    /// <returns>The name.</returns>
    private static TextBlock RowName(Window window, object row)
    {
        var badge = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("badge") && t.DataContext == row);
        return ((Panel)badge.Parent!).Children.OfType<TextBlock>().Single(t => t != badge);
    }

    /// <summary>
    /// Reads the color of a solid brush.
    /// </summary>
    /// <param name="brush">The brush.</param>
    /// <returns>The color.</returns>
    private static Avalonia.Media.Color Color(Avalonia.Media.IBrush? brush)
    {
        return brush.Should().BeAssignableTo<Avalonia.Media.ISolidColorBrush>().Which.Color;
    }

    /// <summary>
    /// Reads the color of a design token in the theme of a window.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="key">The resource key.</param>
    /// <returns>The color.</returns>
    private static Avalonia.Media.Color Token(Window window, string key)
    {
        Avalonia.Application.Current!.TryGetResource(key, window.ActualThemeVariant, out var value).Should().BeTrue();
        return Color((Avalonia.Media.IBrush)value!);
    }

    /// <summary>
    /// Opens the context menu of the tree row of an account.
    /// </summary>
    /// <param name="window">The main window.</param>
    /// <param name="account">The account node.</param>
    /// <returns>The opened menu.</returns>
    private static ContextMenu OpenAccountMenu(MainWindow window, AccountNodeViewModel account)
    {
        var row = UiQueries.MenuOwner(window, account);
        var menu = row.ContextMenu!;
        menu.Open(row);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return menu;
    }
}
