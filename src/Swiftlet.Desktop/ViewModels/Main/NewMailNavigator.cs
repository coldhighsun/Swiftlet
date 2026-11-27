using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// Steps through the messages that arrived while the window was away, newest first, so the reading pane can offer
/// "previous" and "next" within that fixed set.
/// </summary>
public sealed partial class NewMailNavigator : ObservableObject
{
    /// <summary>
    /// The identifiers of the new messages, newest first; fixed when the navigator was created.
    /// </summary>
    private readonly IReadOnlyList<Guid> _ids;

    /// <summary>
    /// Tells whether a message can be shown now (it is still listed).
    /// </summary>
    private readonly Func<Guid, bool> _isAvailable;

    /// <summary>
    /// Shows a message in the reading pane.
    /// </summary>
    private readonly Action<Guid> _show;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The message being shown, or <see cref="Guid.Empty"/> before the first one was chosen.
    /// </summary>
    private Guid _currentId;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewMailNavigator"/> class.
    /// </summary>
    /// <param name="ids">The identifiers of the new messages, newest first.</param>
    /// <param name="isAvailable">Tells whether a message can be shown now.</param>
    /// <param name="show">Shows a message in the reading pane.</param>
    /// <param name="localizer">The localizer.</param>
    public NewMailNavigator(IReadOnlyList<Guid> ids, Func<Guid, bool> isAvailable, Action<Guid> show, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _ids = ids;
        _isAvailable = isAvailable;
        _show = show;
        _localizer = localizer;
    }

    /// <summary>
    /// Gets the number of new messages that can still be shown.
    /// </summary>
    public int Count => _ids.Count(_isAvailable);

    /// <summary>
    /// Gets the text that says which new message is shown, for example "2 / 5".
    /// </summary>
    public string PositionText => _localizer.Format("Notification_Position", Math.Max(Position, 1), Count);

    /// <summary>
    /// Gets the one-based position of the shown message among the available new ones, or 0 when it is not one of them.
    /// </summary>
    private int Position
    {
        get
        {
            var position = 0;
            foreach (var id in _ids.Where(_isAvailable))
            {
                position++;
                if (id == _currentId)
                {
                    return position;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a newer message than the shown one is available.
    /// </summary>
    private bool CanShowPrevious => Neighbour(-1) is not null;

    /// <summary>
    /// Gets a value indicating whether an older message than the shown one is available.
    /// </summary>
    private bool CanShowNext => Neighbour(1) is not null;

    /// <summary>
    /// Tells whether a message belongs to the set.
    /// </summary>
    /// <param name="id">The message identifier.</param>
    /// <returns><see langword="true"/> when the message is one of the new ones.</returns>
    public bool Contains(Guid id)
    {
        return _ids.Contains(id);
    }

    /// <summary>
    /// Remembers which message is shown, after the user chose one of the set by other means.
    /// </summary>
    /// <param name="id">The message identifier.</param>
    public void Follow(Guid id)
    {
        _currentId = id;
        Refresh();
    }

    /// <summary>
    /// Shows the newest available message of the set.
    /// </summary>
    /// <returns><see langword="true"/> when there was one to show.</returns>
    public bool ShowFirst()
    {
        var first = _ids.Where(_isAvailable).Cast<Guid?>().FirstOrDefault();
        if (first is not { } id)
        {
            return false;
        }

        _currentId = id;
        _show(id);
        Refresh();
        return true;
    }

    /// <summary>
    /// Re-reads the position and the state of the buttons, after the list or the language changed.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(PositionText));
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Shows the next newer message of the set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private void Previous()
    {
        Step(-1);
    }

    /// <summary>
    /// Shows the next older message of the set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private void Next()
    {
        Step(1);
    }

    /// <summary>
    /// Moves to a neighbour of the shown message.
    /// </summary>
    /// <param name="direction">-1 for a newer message, 1 for an older one.</param>
    private void Step(int direction)
    {
        if (Neighbour(direction) is not { } id)
        {
            return;
        }

        _currentId = id;
        _show(id);
        Refresh();
    }

    /// <summary>
    /// Finds the closest available message on one side of the shown one.
    /// </summary>
    /// <param name="direction">-1 for a newer message, 1 for an older one.</param>
    /// <returns>The message identifier, or <see langword="null"/> when there is none that way.</returns>
    private Guid? Neighbour(int direction)
    {
        var index = _ids.ToList().IndexOf(_currentId);
        if (index < 0)
        {
            return null;
        }

        for (var i = index + direction; i >= 0 && i < _ids.Count; i += direction)
        {
            if (_isAvailable(_ids[i]))
            {
                return _ids[i];
            }
        }

        return null;
    }
}
