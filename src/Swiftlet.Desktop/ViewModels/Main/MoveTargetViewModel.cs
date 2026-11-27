using System.Windows.Input;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// An entry of the "move to" menu: one folder the selected messages can be moved to.
/// </summary>
/// <param name="folder">The folder.</param>
/// <param name="command">The command moving the selection; it receives the folder node as parameter.</param>
public sealed class MoveTargetViewModel(FolderNodeViewModel folder, ICommand command)
{
    /// <summary>
    /// Gets the target folder.
    /// </summary>
    public FolderNodeViewModel Folder { get; } = folder;

    /// <summary>
    /// Gets the command moving the selection; it receives the folder node as parameter.
    /// </summary>
    public ICommand Command { get; } = command;

    /// <summary>
    /// Gets the folder name as shown in the tree.
    /// </summary>
    public string Name => Folder.Name;
}
