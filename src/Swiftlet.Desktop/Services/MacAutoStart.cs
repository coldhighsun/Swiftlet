using System.Xml;
using System.Xml.Linq;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Starts the application at sign-in with a launch agent of the current user. It takes effect at the next sign-in.
/// </summary>
/// <param name="directory">The launch agents directory.</param>
/// <param name="executablePath">The executable to start.</param>
public sealed class MacAutoStart(string directory, string executablePath) : IAutoStart
{
    /// <summary>
    /// The label of the launch agent, after the bundle identifier in <c>installer/macos/Info.plist</c>.
    /// </summary>
    internal const string Label = "io.github.coldhighsun.swiftlet.autostart";

    /// <summary>
    /// The property list file.
    /// </summary>
    private readonly string _path = Path.Combine(directory, Label + ".plist");

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public bool IsEnabled()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        XElement? dictionary;
        try
        {
            // A property list names an Apple DTD, which is skipped rather than fetched.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(_path, settings);
            dictionary = XDocument.Load(reader).Root?.Element("dict");
        }
        catch (XmlException)
        {
            // A damaged or binary file is not an entry this application wrote; switching it on replaces it.
            return false;
        }

        if (dictionary is null || ValueOf(dictionary, "Disabled")?.Name.LocalName == "true")
        {
            return false;
        }

        return ValueOf(dictionary, "RunAtLoad")?.Name.LocalName == "true"
            && ValueOf(dictionary, "ProgramArguments")?.Element("string")?.Value == executablePath;
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Deleting a file that does not exist is fine, but a missing directory would throw.
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return;
        }

        var document = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    new XElement("key", "Label"),
                    new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", new XElement("string", executablePath)),
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    // Only in a graphical session: the application cannot run in a remote shell.
                    new XElement("key", "LimitLoadToSessionType"),
                    new XElement("string", "Aqua"),
                    new XElement("key", "ProcessType"),
                    new XElement("string", "Interactive"))));
        Directory.CreateDirectory(directory);
        document.Save(_path);
    }

    /// <summary>
    /// Finds the value that follows a key in a property list dictionary.
    /// </summary>
    /// <param name="dictionary">The dictionary element.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value element, or <see langword="null"/> when the key is missing.</returns>
    private static XElement? ValueOf(XElement dictionary, string key)
    {
        return dictionary.Elements("key").FirstOrDefault(e => e.Value == key)?.ElementsAfterSelf().FirstOrDefault();
    }
}
