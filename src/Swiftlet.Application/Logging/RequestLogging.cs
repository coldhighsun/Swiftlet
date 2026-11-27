namespace Swiftlet.Application.Logging;

/// <summary>
/// A command or query about one account; its log lines are tagged with the account.
/// </summary>
public interface IAccountRequest
{
    /// <summary>
    /// Gets the account identifier.
    /// </summary>
    Guid AccountId { get; }
}

/// <summary>
/// A command or query about one folder; its log lines are tagged with the account and the folder.
/// </summary>
public interface IFolderRequest : IAccountRequest
{
    /// <summary>
    /// Gets the folder identifier.
    /// </summary>
    Guid FolderId { get; }
}

/// <summary>
/// A command or query that adds details to its log line.
/// </summary>
public interface ILogDetails
{
    /// <summary>
    /// Describes the request for the log. Only identifiers, names, counts and switches may be included: never passwords,
    /// message subjects or contents, recipients, attachment names or search texts.
    /// </summary>
    /// <param name="names">Resolves identifiers to names.</param>
    /// <returns>The details.</returns>
    string DescribeForLog(LogNames names);
}
