using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The Avalonia implementation of <see cref="IDialogService"/>.
/// </summary>
/// <param name="services">The service provider used to resolve view models.</param>
public sealed class AvaloniaDialogService(IServiceProvider services) : IDialogService
{
    /// <summary>
    /// Gets the classic desktop lifetime of the running application.
    /// </summary>
    private static IClassicDesktopStyleApplicationLifetime? Lifetime =>
        Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>
    /// Gets the active window, falling back to the main window.
    /// </summary>
    private static Window? ActiveWindow => Lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? Lifetime?.MainWindow;

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string okText)
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return false;
        }

        var localizer = services.GetRequiredService<ILocalizer>();
        // Cancel, not the confirming button, answers Enter: the confirmed action may be destructive.
        var ok = new Button { Content = okText, MinWidth = 80 };
        var cancel = new Button { Content = localizer["Common_Cancel"], IsCancel = true, IsDefault = true, MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { ok, cancel },
        };
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
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
    public async Task ShowSettingsAsync()
    {
        var owner = ActiveWindow;
        if (owner is null)
        {
            return;
        }

        var viewModel = services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        await window.ShowDialog(owner);
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
    public Task ShowComposeAsync(IReadOnlyList<MailAccount> accounts, ComposeRequest request)
    {
        var viewModel = new ComposeViewModel(
            services.GetRequiredService<IRequestDispatcher>(),
            this,
            services.GetRequiredService<ILocalizer>(),
            accounts,
            request);
        var window = new ComposeWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Show();
        return Task.CompletedTask;
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
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, HtmlPreviewFile.Build(html));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
