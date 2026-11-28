using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.Html;

/// <summary>
/// Owns the temporary files that other programs read: mail bodies viewed in an external browser and opened attachments.
/// They are not encrypted, so they are kept in one directory per run that is deleted on exit; the other program may read
/// them at any time before that.
/// </summary>
/// <param name="root">The directory the preview directory is created in.</param>
/// <param name="logger">The logger; optional.</param>
public sealed partial class HtmlPreviewStore(string root, ILogger<HtmlPreviewStore>? logger = null)
{
    /// <summary>
    /// The name prefix of the preview directories.
    /// </summary>
    private const string DirectoryPrefix = "swiftlet-preview-";

    /// <summary>
    /// The preview directory of this run; null until the first preview is written.
    /// </summary>
    private string? _directory;

    /// <summary>
    /// Writes a mail body to a new preview file.
    /// </summary>
    /// <param name="html">The HTML body of the message.</param>
    /// <returns>The path of the file.</returns>
    public string Write(string html)
    {
        var document = HtmlPreviewFile.Build(html);
        _directory ??= Path.Combine(root, $"{DirectoryPrefix}{Guid.NewGuid():N}");

        // Repeated on every write because a second instance or a temp cleaner may have removed the directory.
        CreatePrivateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.html");
        File.WriteAllText(path, document);
        return path;
    }

    /// <summary>
    /// Writes an attachment that is about to be opened, under its own name in a directory of its own.
    /// </summary>
    /// <param name="fileName">The name from the message; it is made safe before use.</param>
    /// <param name="content">The file content.</param>
    /// <returns>The path of the file.</returns>
    public async Task<string> WriteAttachmentAsync(string fileName, ReadOnlyMemory<byte> content)
    {
        _directory ??= Path.Combine(root, $"{DirectoryPrefix}{Guid.NewGuid():N}");
        CreatePrivateDirectory(_directory);
        var directory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, AttachmentFiles.SafeName(fileName));
        await File.WriteAllBytesAsync(path, content);
        AttachmentFiles.MarkFromInternet(path, logger);
        return path;
    }

    /// <summary>
    /// Deletes the previews written by this instance.
    /// </summary>
    public void Clear()
    {
        if (_directory is not null)
        {
            TryDelete(_directory, DeleteDirectory);
        }
    }

    /// <summary>
    /// Deletes previews left behind by earlier runs, including the single files written by earlier versions.
    /// </summary>
    public void RemoveLeftovers()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, $"{DirectoryPrefix}*"))
        {
            TryDelete(directory, DeleteDirectory);
        }

        foreach (var file in Directory.EnumerateFiles(root, "swiftlet-*.html"))
        {
            if (LegacyFileName().IsMatch(Path.GetFileName(file)))
            {
                TryDelete(file, File.Delete);
            }
        }
    }

    /// <summary>
    /// Creates a directory that only the current user can access; the Windows temporary directory is already per user.
    /// </summary>
    /// <param name="path">The directory path.</param>
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Deletes a directory with its content.
    /// </summary>
    /// <param name="path">The directory path.</param>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// Matches the name earlier versions gave their preview files.
    /// </summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^swiftlet-[0-9a-f]{32}\\.html$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LegacyFileName();

    /// <summary>
    /// Deletes one entry, keeping it when the system refuses.
    /// </summary>
    /// <param name="path">The entry to delete.</param>
    /// <param name="delete">The deletion to run.</param>
    private void TryDelete(string path, Action<string> delete)
    {
        try
        {
            delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file is still open, or belongs to another user of a shared temporary directory. Cleaning up must
            // not stop startup or exit; the next start tries again.
            logger?.LogWarning(ex, "Could not delete the mail preview {Path}", path);
        }
    }
}
