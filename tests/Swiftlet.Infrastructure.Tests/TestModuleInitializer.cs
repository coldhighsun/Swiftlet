using System.Runtime.CompilerServices;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Prepares the test process the way the application's entry point prepares its own.
/// </summary>
internal static class TestModuleInitializer
{
    /// <summary>
    /// Registers the legacy code pages before any test runs. Test classes run in parallel and in no fixed order, so a
    /// registration made by one class can come after another class has already used MimeKit, which is too late.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        MailEncodings.Register();
    }
}
