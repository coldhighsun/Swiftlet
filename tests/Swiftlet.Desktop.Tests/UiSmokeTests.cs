using AwesomeAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
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
    /// Verifies the reading pane hosts the HTML view (and hides the plain-text body) for a message with an HTML body.
    /// </summary>
    [Fact]
    public async Task MainWindow_HtmlMessageSelected_ShowsHtmlBodyView()
    {
        await Session.Dispatch(
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
                window.GetVisualDescendants().OfType<SelectableTextBlock>().Single().IsVisible.Should().BeFalse();
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
        await Session.Dispatch(
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
                window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).Should().Contain(delete);
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
        await Session.Dispatch(
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
        await Session.Dispatch(
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
        await Session.Dispatch(
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
        await Session.Dispatch(
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
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the settings window shows the checkbox, its hint and the close button, in both languages.
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
                var viewModel = new SettingsViewModel(new FakeAppSettings(), LocalizationManager.Instance, new FakeRemoteImageRules());
                var window = new SettingsWindow { DataContext = viewModel };
                Render(window, $"settings-{language}", language);

                window.Title.Should().Be(title);
                var boxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
                boxes.Should().HaveCount(3);
                var box = boxes.Single(b => b.Content as string == checkbox);
                box.IsChecked.Should().BeFalse();
                boxes.Where(b => b != box).Should().OnlyContain(b => b.IsChecked == true, "the history downloads are on by default");
                window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).Should().Contain(close);
                window.GetVisualDescendants().OfType<TextBlock>().Count(t => !string.IsNullOrEmpty(t.Text) && t.Text.Length > 40).Should().BeGreaterThan(0);
                window.Close();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the toolbar label flyout lists the labels and ticking one sends the set-label command.
    /// </summary>
    [Fact]
    public async Task MainWindow_LabelsFlyout_TickingLabelSendsCommand()
    {
        await Session.Dispatch(
            async () =>
            {
                LocalizationManager.Instance.SetPreference("en");
                var scenario = new MainWindowViewModelTests.Scenario();
                scenario.Localizer.SetPreference("en");
                await scenario.StartAsync();
                scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
                var window = new MainWindow { DataContext = scenario.ViewModel };
                Render(window, "main-window-labels-flyout", "en");

                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Labels");
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
    public async Task MainWindow_AccountNodeContextMenu_OffersDeleteAndOAuthOnlyReauthenticate()
    {
        await Session.Dispatch(
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

                var passwordMenu = OpenAccountMenu(window, scenario.Account.EmailAddress);
                passwordMenu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header).Should().Equal("Remove account");
                passwordMenu.Close();
                var oauthMenu = OpenAccountMenu(window, oauth.EmailAddress);
                var items = oauthMenu.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
                items.Select(i => i.Header).Should().Equal("Remove account", "Re-sign in");
                items.Should().OnlyContain(i => i.Command != null && i.CommandParameter != null);
                items[1].Command!.Execute(items[1].CommandParameter);
                await scenario.ViewModel.WhenIdleAsync();

                scenario.Dispatcher.Sent<Swiftlet.Application.Accounts.ReauthenticateOAuthAccountCommand>()
                    .Should().ContainSingle().Which.AccountId.Should().Be(oauth.Id);
                oauthMenu.Close();
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Opens the context menu of the tree node showing an account address.
    /// </summary>
    /// <param name="window">The main window.</param>
    /// <param name="address">The account address.</param>
    /// <returns>The opened menu.</returns>
    private static ContextMenu OpenAccountMenu(MainWindow window, string address)
    {
        var text = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == address);
        var menu = text.ContextMenu;
        menu.Should().NotBeNull();
        menu!.Open(text);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return menu;
    }
}
