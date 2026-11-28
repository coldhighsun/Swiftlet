using System.Runtime.Versioning;
using System.Text;
using AwesomeAssertions;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="AttachmentFiles"/>.
/// </summary>
public class AttachmentFilesTests
{
    /// <summary>
    /// Verifies a file name from a message cannot leave its directory or disguise itself.
    /// </summary>
    /// <param name="fileName">The name in the message.</param>
    /// <param name="expected">The name written to disk.</param>
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("报告 (终稿).docx", "报告 (终稿).docx")]
    [InlineData(@"..\..\startup\evil.txt", "evil.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("invoice‮fdp.exe", "invoicefdp.exe")]
    [InlineData("line\r\nbreak.txt", "linebreak.txt")]
    [InlineData("setup.exe. . ", "setup.exe")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("..", "attachment")]
    [InlineData("", "attachment")]
    [InlineData("folder/", "attachment")]
    public void SafeName_UntrustedName_ReturnsPlainFileName(string fileName, string expected)
    {
        AttachmentFiles.SafeName(fileName).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a very long name is shortened to what every file system accepts, keeping the extension.
    /// </summary>
    [Fact]
    public void SafeName_LongName_IsShortenedKeepingExtension()
    {
        var name = AttachmentFiles.SafeName(new string('报', 300) + ".docx");

        Encoding.UTF8.GetByteCount(name).Should().BeLessThanOrEqualTo(200);
        name.Should().EndWith("报.docx");
    }

    /// <summary>
    /// Verifies documents and media can be opened.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    [Theory]
    [InlineData("photo.JPG")]
    [InlineData("photo.heic")]
    [InlineData("report.pdf")]
    [InlineData("letter.docx")]
    [InlineData("notes.txt")]
    [InlineData("backup.zip")]
    [InlineData("movie.mp4")]
    [InlineData("invite.ics")]
    [InlineData("page.html")]
    public void CanOpen_Document_ReturnsTrue(string fileName)
    {
        AttachmentFiles.CanOpen(fileName).Should().BeTrue();
    }

    /// <summary>
    /// Verifies programs, scripts, shortcuts, installers, disk images and unknown files are not opened.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("invoice.pdf.exe")]
    [InlineData("setup.exe.")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("run.ps1")]
    [InlineData("macro.vbs")]
    [InlineData("script.js")]
    [InlineData("page.hta")]
    [InlineData("package.msi")]
    [InlineData("shortcut.lnk")]
    [InlineData("link.url")]
    [InlineData("keys.reg")]
    [InlineData("tool.jar")]
    [InlineData("disk.iso")]
    [InlineData("tool.py")]
    [InlineData("install.sh")]
    [InlineData("Setup.dmg")]
    [InlineData("app.desktop")]
    [InlineData("package.deb")]
    [InlineData("tool.AppImage")]
    [InlineData("Makefile")]
    [InlineData("odd.")]
    public void CanOpen_ProgramOrUnknown_ReturnsFalse(string fileName)
    {
        AttachmentFiles.CanOpen(fileName).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a file is marked as downloaded from the internet, which makes Windows warn before running it.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void MarkFromInternet_OnWindows_AddsZoneIdentifier()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Zone identifiers exist on Windows only.");
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "content");

        try
        {
            AttachmentFiles.MarkFromInternet(path);

            File.ReadAllText(path + ":Zone.Identifier").Should().Contain("ZoneId=3");
            File.ReadAllText(path).Should().Be("content");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
