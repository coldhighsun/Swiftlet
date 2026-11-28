using CommunityToolkit.Mvvm.Input;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// A window showing one message. The actions are carried out by the main window view model, which owns the accounts, the
/// list and the status line.
/// </summary>
public sealed partial class MessageWindowViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageWindowViewModel"/> class.
    /// </summary>
    /// <param name="main">The main window view model.</param>
    /// <param name="item">The message to show.</param>
    public MessageWindowViewModel(MainWindowViewModel main, MessageItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(item);
        Main = main;
        Item = item;
    }

    /// <summary>
    /// Raised when the window should close, because its message was archived or deleted.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Gets the main window view model.
    /// </summary>
    public MainWindowViewModel Main { get; }

    /// <summary>
    /// Gets the shown message.
    /// </summary>
    public MessageItemViewModel Item { get; }

    /// <summary>
    /// Gets a value indicating whether the message can be archived.
    /// </summary>
    private bool CanArchive => Main.CanArchiveMessage(Item);

    /// <summary>
    /// Tells the main window view model that the window was closed.
    /// </summary>
    public void Detach() => Main.ForgetMessageWindow(this);

    /// <summary>
    /// Opens a compose window replying to the message.
    /// </summary>
    /// <returns>A task that completes when the window has been opened.</returns>
    [RelayCommand]
    private Task ReplyAsync() => Main.ReplyToAsync(Item);

    /// <summary>
    /// Opens a compose window forwarding the message.
    /// </summary>
    /// <returns>A task that completes when the window has been opened.</returns>
    [RelayCommand]
    private Task ForwardAsync() => Main.ForwardMessageAsync(Item);

    /// <summary>
    /// Toggles the star of the message.
    /// </summary>
    /// <returns>A task that completes when the state has been saved.</returns>
    [RelayCommand]
    private Task ToggleFlagAsync() => Main.ToggleFlagOfAsync(Item);

    /// <summary>
    /// Archives the message and closes the window.
    /// </summary>
    /// <returns>A task that completes when the move has been queued or has failed.</returns>
    [RelayCommand(CanExecute = nameof(CanArchive))]
    private async Task ArchiveAsync()
    {
        if (await Main.ArchiveMessageAsync(Item))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Deletes the message after a confirmation and closes the window.
    /// </summary>
    /// <returns>A task that completes when the deletion has been queued, cancelled or has failed.</returns>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await Main.DeleteMessageAsync(Item))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
