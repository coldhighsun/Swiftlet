using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Rules for writing mail attachments to disk and opening them. The file names come from the sender and are untrusted.
/// </summary>
public static class AttachmentFiles
{
    /// <summary>
    /// The name used when nothing usable is left of the original one.
    /// </summary>
    private const string FallbackName = "attachment";

    /// <summary>
    /// The longest file name in UTF-8 bytes; Linux and macOS allow 255, and the path around it needs room on Windows.
    /// </summary>
    private const int MaxNameBytes = 200;

    /// <summary>
    /// The longest extension that is preserved when a name is shortened, dot included.
    /// </summary>
    private const int MaxExtensionChars = 20;

    /// <summary>
    /// Characters Windows refuses in file names; replaced on every platform.
    /// </summary>
    private const string InvalidCharacters = "<>:\"/\\|?*";

    /// <summary>
    /// File names Windows reserves for devices, with or without an extension.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Extensions of files that run code, install something or point somewhere else when they are opened.
    /// </summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows programs, libraries and installers.
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msc", ".dll", ".sys", ".drv", ".ocx", ".msi", ".msp", ".mst", ".msu",
        ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application", ".appref-ms", ".gadget",
        ".xbap", ".xll", ".xlam", ".ppa", ".ppam", ".wll", ".air",

        // Windows scripts.
        ".bat", ".cmd", ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".psm1", ".psd1", ".pssc", ".cdxml",
        ".vb", ".vbs", ".vbe", ".js", ".jse", ".ws", ".wsf", ".wsh", ".wsc", ".sct", ".hta", ".chm", ".hlp",

        // Windows shortcuts, settings and files that change the system.
        ".lnk", ".url", ".website", ".scf", ".shb", ".shs", ".xnk", ".reg", ".inf", ".ins", ".isp", ".its", ".prf",
        ".diagcab", ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms", ".theme", ".themepack",
        ".ade", ".adp", ".mde", ".accde", ".mam", ".maq", ".mar",

        // Disk images, which hide what they contain from the download mark.
        ".iso", ".img", ".vhd", ".vhdx", ".wim",

        // Other runtimes.
        ".jar", ".jnlp", ".py", ".pyc", ".pyw", ".pyz", ".pyzw", ".rb", ".pl", ".php", ".tcl", ".apk",

        // Unix and macOS.
        ".sh", ".bash", ".zsh", ".csh", ".ksh", ".fish", ".command", ".tool", ".run", ".bin", ".so", ".dylib",
        ".desktop", ".appimage", ".deb", ".rpm", ".snap", ".flatpak", ".flatpakref", ".flatpakrepo", ".app", ".pkg",
        ".mpkg", ".dmg", ".scpt", ".applescript", ".workflow", ".action", ".terminal", ".webloc", ".inetloc",
        ".fileloc",
    };

    /// <summary>
    /// Turns the name an attachment carries into a plain file name that is safe to create.
    /// </summary>
    /// <param name="fileName">The name from the message.</param>
    /// <returns>A name without directories, control or direction characters and trailing dots or spaces.</returns>
    public static string SafeName(string? fileName)
    {
        var text = fileName ?? string.Empty;
        text = text[(text.LastIndexOfAny(['/', '\\']) + 1)..];

        Span<char> units = stackalloc char[2];
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            // Format characters include the direction overrides that make "gpj.exe" read as "exe.jpg".
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            if (rune.IsBmp && InvalidCharacters.Contains((char)rune.Value, StringComparison.Ordinal))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(units[..rune.EncodeToUtf16(units)]);
            }
        }

        var name = Shorten(Trim(builder.ToString()));
        if (name.Length == 0)
        {
            return FallbackName;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }

    /// <summary>
    /// Gets a value indicating whether an attachment may be opened from the application.
    /// </summary>
    /// <param name="fileName">The name from the message.</param>
    /// <returns><see langword="false"/> for programs, scripts, installers and the like, and for files without an extension.</returns>
    public static bool CanOpen(string? fileName)
    {
        var extension = Path.GetExtension(SafeName(fileName));
        return extension.Length > 1 && !BlockedExtensions.Contains(extension);
    }

    /// <summary>
    /// Marks a file as coming from the internet, so that Windows warns before running it and Office opens it protected.
    /// Other platforms have no such mark here.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="logger">Reports a mark that could not be written; optional.</param>
    public static void MarkFromInternet(string path, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // File systems without alternate data streams (FAT, some network shares) cannot carry the mark; the file
            // itself is fine and browsers behave the same there.
            logger?.LogWarning(ex, "Could not mark an attachment as downloaded from the internet");
        }
    }

    /// <summary>
    /// Removes the spaces around a name and the dots at its end, which Windows drops silently.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The trimmed name.</returns>
    private static string Trim(string name)
    {
        return name.TrimStart(' ').TrimEnd(' ', '.');
    }

    /// <summary>
    /// Shortens a name to <see cref="MaxNameBytes"/>, keeping a reasonably short extension.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name, shortened if necessary.</returns>
    private static string Shorten(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxNameBytes)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > MaxExtensionChars)
        {
            extension = string.Empty;
        }

        var remaining = MaxNameBytes - Encoding.UTF8.GetByteCount(extension);
        Span<char> units = stackalloc char[2];
        var stem = new StringBuilder();
        foreach (var rune in name[..^extension.Length].EnumerateRunes())
        {
            remaining -= rune.Utf8SequenceLength;
            if (remaining < 0)
            {
                break;
            }

            stem.Append(units[..rune.EncodeToUtf16(units)]);
        }

        return Trim(stem.Append(extension).ToString());
    }
}
