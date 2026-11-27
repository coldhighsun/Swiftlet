using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Swiftlet.Application.Logging;
using Swiftlet.Desktop.Hosting;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="LogSetup"/>.
/// </summary>
public sealed class LogSetupTests : IDisposable
{
    /// <summary>
    /// The temporary log directory of the test.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "swiftlet-log-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Verifies the defaults are 10 MB per file and 7 files kept.
    /// </summary>
    [Fact]
    public void Defaults_AreTenMegabytesAndSevenFiles()
    {
        LogSetup.FileSizeLimitBytes.Should().Be(10 * 1024 * 1024);
        LogSetup.RetainedFiles.Should().Be(7);
    }

    /// <summary>
    /// Verifies a file that passes the size limit rolls over to a new file.
    /// </summary>
    [Fact]
    public void Create_SizeLimitExceeded_StartsNewFile()
    {
        using (var logger = LogSetup.Create(_directory, fileSizeLimitBytes: 2000, retainedFiles: 50))
        {
            for (var i = 0; i < 40; i++)
            {
                logger.Information("line {Index} {Padding}", i, new string('x', 100));
            }
        }

        Directory.GetFiles(_directory, "swiftlet-*.log").Length.Should().BeGreaterThan(1);
    }

    /// <summary>
    /// Verifies only the configured number of files is kept.
    /// </summary>
    [Fact]
    public void Create_MoreFilesThanLimit_KeepsOnlyNewest()
    {
        using (var logger = LogSetup.Create(_directory, fileSizeLimitBytes: 500, retainedFiles: 3))
        {
            for (var i = 0; i < 60; i++)
            {
                logger.Information("line {Index} {Padding}", i, new string('x', 100));
            }
        }

        Directory.GetFiles(_directory, "swiftlet-*.log").Length.Should().Be(3);
    }

    /// <summary>
    /// Verifies Swiftlet debug messages are written while framework noise below Warning is not.
    /// </summary>
    [Fact]
    public void Create_Levels_WritesSwiftletDebugButNotFrameworkInformation()
    {
        using (var logger = LogSetup.Create(_directory))
        {
            logger.ForContext("SourceContext", "Swiftlet.Application.Thing").Debug("swiftlet-debug-marker");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Query").Information("framework-info-marker");
        }

        var text = string.Concat(Directory.GetFiles(_directory).Select(File.ReadAllText));
        text.Should().Contain("swiftlet-debug-marker").And.NotContain("framework-info-marker");
    }

    /// <summary>
    /// Verifies the tags of a mail scope are printed in front of the message, the innermost scope wins, and a line outside
    /// any scope has none.
    /// </summary>
    [Fact]
    public void Create_MailScopes_PrintsTagsBeforeTheMessage()
    {
        using (var serilog = LogSetup.Create(_directory))
        using (var factory = new SerilogLoggerFactory(serilog))
        {
            var logger = factory.CreateLogger("Swiftlet.Test");
            using (logger.BeginMailScope("me@example.com", "INBOX"))
            {
                logger.LogInformation("in folder");
                using (logger.BeginMailScope("you@example.com"))
                {
                    logger.LogInformation("in account");
                }
            }

            logger.LogInformation("outside");
        }

        var text = string.Concat(Directory.GetFiles(_directory).Select(File.ReadAllText));
        text.Should().Contain("[INF] [me@example.com] [INBOX] in folder")
            .And.Contain("[INF] [you@example.com] in account")
            .And.Contain("[INF] outside");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
