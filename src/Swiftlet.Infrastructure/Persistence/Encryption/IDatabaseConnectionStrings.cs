namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Gives the connection string of each database file.
/// </summary>
public interface IDatabaseConnectionStrings
{
    /// <summary>
    /// Gets the connection string of a database file, which may not exist yet.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <returns>The connection string; it may contain the key and must not be logged.</returns>
    string GetConnectionString(string databasePath);
}
