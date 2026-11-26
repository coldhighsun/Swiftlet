using AwesomeAssertions;
using MimeKit;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests that the legacy code pages are registered before anything in the test process uses MimeKit.
/// </summary>
public class MailEncodingsTests
{
    /// <summary>
    /// Verifies MimeKit decodes a GB2312 encoded word although the test registers nothing itself. MimeKit caches the
    /// charsets it cannot resolve for the lifetime of the process, so a late registration leaves mojibake behind.
    /// This class must not register the code pages, otherwise running it alone proves nothing.
    /// </summary>
    [Fact]
    public void Load_Rfc2047GbkSubjectWithoutOwnRegistration_DecodesSubject()
    {
        // Arrange
        using var stream = new MemoryStream("Subject: =?gb2312?B?u+HS6c2o1qo=?=\r\n\r\nbody"u8.ToArray());

        // Act
        using var message = MimeMessage.Load(stream, TestContext.Current.CancellationToken);

        // Assert
        message.Subject.Should().Be("会议通知");
    }
}
