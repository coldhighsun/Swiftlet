using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// A checkable entry of the per-message label menu: one existing label and whether the selected message carries it.
/// </summary>
/// <param name="label">The label.</param>
/// <param name="isAssigned">Whether the selected message carries the label.</param>
/// <param name="toggle">The command run after the entry was ticked or unticked; it receives this entry as parameter.</param>
public sealed partial class MessageLabelChoiceViewModel(Label label, bool isAssigned, ICommand toggle) : ObservableObject
{
    /// <summary>
    /// Gets the label.
    /// </summary>
    public Label Label { get; } = label;

    /// <summary>
    /// Gets the command run after the entry was ticked or unticked; it receives this entry as parameter.
    /// </summary>
    public ICommand ToggleCommand { get; } = toggle;

    /// <summary>
    /// Gets the label name.
    /// </summary>
    public string Name => Label.Name;

    /// <summary>
    /// Gets the brush painting the label color; created on first use so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush Brush => field ??= new SolidColorBrush(Avalonia.Media.Color.Parse(Label.Color));

    /// <summary>
    /// Gets or sets a value indicating whether the message carries the label; while a toggle runs, the desired state.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAssigned { get; set; } = isAssigned;
}
