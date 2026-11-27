namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// Listed messages of one account that are moved to one of its folders.
/// </summary>
/// <param name="AccountId">The account that owns the messages and the folder.</param>
/// <param name="TargetFolderId">The folder to move the messages to.</param>
/// <param name="Messages">The messages.</param>
internal sealed record MoveBatch(Guid AccountId, Guid TargetFolderId, IReadOnlyList<MessageItemViewModel> Messages);
