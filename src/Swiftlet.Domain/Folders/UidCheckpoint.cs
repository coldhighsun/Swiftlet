namespace Swiftlet.Domain.Folders;

/// <summary>
/// Records that at a certain time every message of a folder with a UID up to a boundary was already in it. A message
/// moved into a folder gets a higher UID than all before it, so a few of these tell how long messages have been there.
/// </summary>
/// <param name="Uid">The highest UID the folder held.</param>
/// <param name="ObservedAt">When it held it.</param>
public sealed record UidCheckpoint(uint Uid, DateTimeOffset ObservedAt);
