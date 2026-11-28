using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Swiftlet.Desktop.ViewModels.Ai;
using Swiftlet.Desktop.ViewModels.Compose;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The window for composing a message.
/// </summary>
public partial class ComposeWindow : Window
{
    /// <summary>
    /// The smallest height while the writing help is open, so the message being written stays visible above it.
    /// </summary>
    private const double MinHeightWithAssist = 620;

    /// <summary>
    /// The smallest height without the writing help, as set in the markup.
    /// </summary>
    private readonly double _minHeight;

    /// <summary>
    /// The writing help being followed, if any.
    /// </summary>
    private ComposeAssistViewModel? _assist;

    /// <summary>
    /// The view model being followed, if any.
    /// </summary>
    private ComposeViewModel? _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComposeWindow"/> class.
    /// </summary>
    public ComposeWindow()
    {
        InitializeComponent();
        _minHeight = MinHeight;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_assist is not null)
        {
            _assist.PropertyChanged -= OnAssistChanged;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as ComposeViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        _assist = _viewModel?.Assist;
        if (_assist is not null)
        {
            _assist.PropertyChanged += OnAssistChanged;
        }
    }

    /// <summary>
    /// Moves to the Cc field when it appears.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposeViewModel.IsCcBccShown) && _viewModel!.IsCcBccShown)
        {
            // Posted, because the field cannot take the focus before the binding has made it visible.
            Dispatcher.UIThread.Post(() => CcBox.Focus());
        }
    }

    /// <summary>
    /// Makes room when the writing help opens.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnAssistChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ComposeAssistViewModel.IsOpen))
        {
            return;
        }

        MinHeight = _assist!.IsOpen ? MinHeightWithAssist : _minHeight;
        if (Bounds.Height < MinHeight)
        {
            Height = MinHeight;
        }
    }
}
