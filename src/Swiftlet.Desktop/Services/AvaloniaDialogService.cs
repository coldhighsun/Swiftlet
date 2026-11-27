using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Accounts;
using Swiftlet.Desktop.Html;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Desktop.ViewModels.Contacts;
using Swiftlet.Desktop.ViewModels.Settings;
using Swiftlet.Desktop.Views;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The Avalonia implementation of <see cref="IDialogService"/>.
/// </summary>
/// <param name="services">The service provider used to resolve view models.</param>
public sealed class AvaloniaDialogService(IServiceProvider services) : IDialogService
{
    /// <summary>
    /// The open settings window, if any.
    /// </summary>
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Gets the classic desktop lifetime of the running application.
    /// </summary>
    private static IClassicDesktopStyleApplicationLifetime? Lifetime =>
        Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>
    /// Gets the active window, falling back to the main window unless it is hidden in the tray: a hidden window cannot own
    /// a dialog.
    /// </summary>
    private static Window? ActiveWindow =>
        Lifetime?.Windows.FirstOrDefault(w => w.IsActive)
        ?? (Lifetime?.MainWindow is { IsVisible: true } main ? main : null);

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string okText)
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return false;
        }

        var localizer = services.GetRequiredService<ILocalizer>();
        var window = ConfirmWindow.Create(title, message, okText, localizer["Common_Cancel"]);
        return await window.ShowDialog<bool>(owner);
    }

    /// <inheritdoc />
    public async Task<bool> ShowAddAccountAsync()
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return false;
        }

        var viewModel = services.GetRequiredService<AddAccountViewModel>();
        var window = new AddAccountWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, added) => window.Close(added);
        return await window.ShowDialog<bool>(owner);
    }

    /// <inheritdoc />
    public async Task<UpdateAccountResult?> ShowEditAccountAsync(MailAccount account, IReadOnlyList<MailFolder> folders)
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return null;
        }

        var viewModel = services.GetRequiredService<EditAccountViewModel>();
        viewModel.Load(account, folders);
        var window = new EditAccountWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, result) => window.Close(result);

        // Closing through the title bar must stop a running verification just like the Cancel button.
        window.Closed += (_, _) => viewModel.CancelPendingSave();
        return await window.ShowDialog<UpdateAccountResult?>(owner);
    }

    /// <inheritdoc />
    public Task ShowSettingsAsync()
    {
        return ShowSettingsAsync(Lifetime?.MainWindow);
    }

    /// <summary>
    /// Shows the settings window: as a dialog of the main window when that is on screen, otherwise on its own, so the
    /// tray menu does not have to bring up the main window. An open settings window is brought to the front instead of
    /// opening a second one.
    /// </summary>
    /// <param name="mainWindow">The main window, if there is one.</param>
    /// <returns>A task that completes when the window has been closed, or at once when it was already open.</returns>
    internal async Task ShowSettingsAsync(Window? mainWindow)
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var viewModel = services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        _settingsWindow = window;
        try
        {
            // A dialog of a minimized window would stay out of sight with it.
            if (mainWindow is { IsVisible: true, WindowState: not WindowState.Minimized })
            {
                await window.ShowDialog(mainWindow);
                return;
            }

            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            window.Activate();
            await closed.Task;
        }
        finally
        {
            _settingsWindow = null;
            viewModel.Detach();
        }
    }

    /// <inheritdoc />
    public async Task ShowLabelManagerAsync()
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return;
        }

        var viewModel = services.GetRequiredService<LabelManagerViewModel>();
        var window = new LabelManagerWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        await viewModel.InitializeAsync();
        await window.ShowDialog(owner);
    }

    /// <inheritdoc />
    public async Task<string?> ShowContactsAsync(ContactsRequest? request)
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return null;
        }

        var viewModel = services.GetRequiredService<ContactsViewModel>();
        var window = new ContactsWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, recipient) => window.Close(recipient);
        await viewModel.InitializeAsync(request);
        return await window.ShowDialog<string?>(owner);
    }

    /// <inheritdoc />
    public async Task<string?> PickContactFileAsync()
    {
        var window = ActiveWindow;
        if (window is null)
        {
            return null;
        }

        var localizer = services.GetRequiredService<ILocalizer>();
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            FileTypeFilter = [new FilePickerFileType(localizer["Contacts_FileType"]) { Patterns = ["*.vcf", "*.csv"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task ShowComposeAsync(IReadOnlyList<MailAccount> accounts, ComposeRequest request)
    {
        var viewModel = new ComposeViewModel(
            services.GetRequiredService<IRequestDispatcher>(),
            this,
            services.GetRequiredService<ILocalizer>(),
            accounts,
            request,
            services.GetService<ILogger<ComposeViewModel>>());
        var window = new ComposeWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Show();
        await viewModel.InitializeAsync();
    }

    /// <inheritdoc />
    public async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        var window = ActiveWindow;
        if (window is null)
        {
            return null;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
        });
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var window = ActiveWindow;
        if (window is null)
        {
            return [];
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <inheritdoc />
    public void ShowHtmlInBrowser(string html)
    {
        var path = services.GetRequiredService<HtmlPreviewStore>().Write(html);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <inheritdoc />
    public async Task<bool> OpenFolderAsync(string path)
    {
        var window = ActiveWindow ?? _settingsWindow;
        var directory = new DirectoryInfo(path);
        if (window is null || !directory.Exists)
        {
            return false;
        }

        return await window.Launcher.LaunchDirectoryInfoAsync(directory);
    }
}
