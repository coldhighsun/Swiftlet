using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Labels;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// A label row in the label manager; the name and color can be edited before saving.
/// </summary>
public sealed partial class LabelItemViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LabelItemViewModel"/> class.
    /// </summary>
    /// <param name="label">The label.</param>
    /// <param name="presetColors">The preset colors offered for the label.</param>
    public LabelItemViewModel(Label label, IReadOnlyList<string> presetColors)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(presetColors);
        Id = label.Id;
        Name = label.Name;
        Color = label.Color;
        ColorOptions = presetColors.Contains(label.Color, StringComparer.OrdinalIgnoreCase) ? presetColors : [.. presetColors, label.Color];
    }

    /// <summary>
    /// Gets the label identifier.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Gets the colors the row offers: the presets plus the current color when it is not a preset.
    /// </summary>
    public IReadOnlyList<string> ColorOptions { get; }

    /// <summary>
    /// Gets or sets the label name.
    /// </summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>
    /// Gets or sets the label color as <c>#RRGGBB</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    public partial string Color { get; set; }

    /// <summary>
    /// Gets the brush painting the color swatch; created on demand so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush Brush => new SolidColorBrush(Avalonia.Media.Color.Parse(Color));
}

/// <summary>
/// The view model of the label manager dialog: add, rename, recolor and delete labels.
/// </summary>
public sealed partial class LabelManagerViewModel : ObservableObject
{
    /// <summary>
    /// The preset colors offered for labels.
    /// </summary>
    private static readonly string[] PresetColors =
    [
        "#E53935", "#FB8C00", "#FDD835", "#43A047", "#00ACC1", "#1E88E5", "#8E24AA", "#D81B60", "#6D4C41", "#757575",
    ];

    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LabelManagerViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="localizer">The localizer.</param>
    public LabelManagerViewModel(IRequestDispatcher dispatcher, ILocalizer localizer)
    {
        _dispatcher = dispatcher;
        _localizer = localizer;
        NewName = string.Empty;
        NewColor = PresetColors[0];
    }

    /// <summary>
    /// Raised when the dialog should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Gets the preset colors offered for new labels.
    /// </summary>
    public IReadOnlyList<string> ColorOptions { get; } = PresetColors;

    /// <summary>
    /// Gets the labels, ordered by name.
    /// </summary>
    public ObservableCollection<LabelItemViewModel> Labels { get; } = [];

    /// <summary>
    /// Gets or sets the name of the label to add.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewName { get; set; }

    /// <summary>
    /// Gets or sets the color of the label to add, as <c>#RRGGBB</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBrush))]
    public partial string NewColor { get; set; }

    /// <summary>
    /// Gets the brush painting the swatch of the label to add.
    /// </summary>
    public IBrush NewBrush => new SolidColorBrush(Avalonia.Media.Color.Parse(NewColor));

    /// <summary>
    /// Gets or sets the localized error of the last failed operation, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Loads the labels.
    /// </summary>
    /// <returns>A task that completes when the list is shown.</returns>
    public async Task InitializeAsync()
    {
        await RunAsync(ReloadAsync);
    }

    /// <summary>
    /// Adds a label from the new-label fields.
    /// </summary>
    /// <returns>A task that completes when the label has been added.</returns>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        await RunAsync(async () =>
        {
            await _dispatcher.SendAsync(new CreateLabelCommand(NewName, NewColor));
            NewName = string.Empty;
            await ReloadAsync();
        });
    }

    /// <summary>
    /// Saves the edited name and color of a label.
    /// </summary>
    /// <param name="item">The edited row.</param>
    /// <returns>A task that completes when the label has been saved.</returns>
    [RelayCommand]
    private async Task SaveAsync(LabelItemViewModel? item)
    {
        ArgumentNullException.ThrowIfNull(item);
        await RunAsync(async () => await _dispatcher.SendAsync(new UpdateLabelCommand(item.Id, item.Name, item.Color)));
    }

    /// <summary>
    /// Deletes a label; messages that carried it are kept.
    /// </summary>
    /// <param name="item">The row to delete.</param>
    /// <returns>A task that completes when the label has been deleted.</returns>
    [RelayCommand]
    private async Task DeleteAsync(LabelItemViewModel? item)
    {
        ArgumentNullException.ThrowIfNull(item);
        await RunAsync(async () =>
        {
            await _dispatcher.SendAsync(new DeleteLabelCommand(item.Id));
            Labels.Remove(item);
        });
    }

    /// <summary>
    /// Asks the dialog to close.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets a value indicating whether a label can be added.
    /// </summary>
    private bool CanAdd => !string.IsNullOrWhiteSpace(NewName);

    /// <summary>
    /// Replaces the rows with the stored labels.
    /// </summary>
    /// <returns>A task that completes when the rows are replaced.</returns>
    private async Task ReloadAsync()
    {
        var labels = await _dispatcher.SendAsync(new GetLabelsQuery());
        Labels.Clear();
        foreach (var label in labels)
        {
            Labels.Add(new LabelItemViewModel(label, PresetColors));
        }
    }

    /// <summary>
    /// Runs an operation, clearing the previous error and turning failures into localized error text.
    /// </summary>
    /// <param name="action">The operation.</param>
    /// <returns>A task that completes when the operation has finished or failed.</returns>
    private async Task RunAsync(Func<Task> action)
    {
        ErrorText = null;
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            // The label commands signal a duplicate name this way; the exception text is English-only, so show localized text.
            ErrorText = _localizer["Labels_ErrorDuplicate"];
        }
        catch (ArgumentException)
        {
            // The domain rejects a blank or too long name or a malformed color.
            ErrorText = _localizer["Labels_ErrorInvalid"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: report storage failures to the user instead of crashing the dialog.
            ErrorText = _localizer.Format("Error_Format", _localizer["Labels_ErrorFailed"], ex.Message);
        }
    }
}
