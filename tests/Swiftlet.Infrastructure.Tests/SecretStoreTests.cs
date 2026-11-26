using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Infrastructure.Security;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// A command runner answering from a delegate and recording every call.
/// </summary>
/// <param name="respond">Produces the result for a call.</param>
internal sealed class FakeCommandRunner(Func<string, IReadOnlyList<string>, string?, CommandResult> respond) : ICommandRunner
{
    /// <summary>
    /// Gets the recorded calls.
    /// </summary>
    public List<(string FileName, IReadOnlyList<string> Arguments, string? StandardInput)> Calls { get; } = [];

    /// <inheritdoc />
    public Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((fileName, arguments, standardInput));
        return Task.FromResult(respond(fileName, arguments, standardInput));
    }
}

/// <summary>
/// Tests for the platform secret stores.
/// </summary>
public class SecretStoreTests
{
    /// <summary>
    /// Creates a result with the given exit code and output.
    /// </summary>
    /// <param name="exitCode">The exit code.</param>
    /// <param name="output">The standard output.</param>
    /// <param name="error">The standard error.</param>
    /// <returns>The result.</returns>
    private static CommandResult Result(int exitCode, string output = "", string error = "")
    {
        return new CommandResult(exitCode, output, error);
    }

    /// <summary>
    /// Verifies the Windows store encrypts values, round-trips Unicode and removes secrets (runs on Windows only).
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsSecretStore_SetGetRemove_RoundTripsEncrypted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is only available on Windows.");
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        try
        {
            var store = new WindowsSecretStore(directory);
            (await store.GetAsync("k", ct)).Should().BeNull();

            await store.SetAsync("k", "pässwörd-密码", ct);

            (await store.GetAsync("k", ct)).Should().Be("pässwörd-密码");
            var file = Directory.GetFiles(directory).Should().ContainSingle().Subject;
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, ct)).Should().NotContain("pässwörd");

            await new WindowsSecretStore(directory).RemoveAsync("k", ct);
            (await store.GetAsync("k", ct)).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies replacing a secret goes through a temporary file that does not stay behind, even when an interrupted
    /// earlier write left one (runs on Windows only).
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsSecretStore_Overwrite_LeavesNoTemporaryFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is only available on Windows.");
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        try
        {
            var store = new WindowsSecretStore(directory);
            await store.SetAsync("k", "first", ct);
            var file = Directory.GetFiles(directory).Should().ContainSingle().Subject;
            await File.WriteAllTextAsync($"{file}.tmp", "half written", ct);

            await store.SetAsync("k", "second", ct);

            Directory.GetFiles(directory).Should().Equal(file);
            (await store.GetAsync("k", ct)).Should().Be("second");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies the macOS store builds the expected <c>security</c> commands and Base64-encodes values.
    /// </summary>
    [Fact]
    public async Task MacKeychainSecretStore_Operations_UseSecurityCommandWithEncodedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = Convert.ToBase64String(Encoding.UTF8.GetBytes("pässwörd-密码"));
        var runner = new FakeCommandRunner((_, args, _) => args[0] switch
        {
            "find-generic-password" => Result(0, stored + "\n"),
            _ => Result(0),
        });
        var store = new MacKeychainSecretStore(runner);

        await store.SetAsync("account:1", "pässwörd-密码", ct);
        var value = await store.GetAsync("account:1", ct);
        await store.RemoveAsync("account:1", ct);

        value.Should().Be("pässwörd-密码");
        runner.Calls.Select(c => c.FileName).Should().OnlyContain(f => f == "security");
        runner.Calls[0].Arguments.Should().Equal("add-generic-password", "-U", "-s", "Swiftlet", "-a", "account:1", "-w", stored);
        runner.Calls[1].Arguments.Should().Equal("find-generic-password", "-s", "Swiftlet", "-a", "account:1", "-w");
        runner.Calls[2].Arguments.Should().Equal("delete-generic-password", "-s", "Swiftlet", "-a", "account:1");
    }

    /// <summary>
    /// Verifies missing items are not errors on macOS, while other failures are reported.
    /// </summary>
    [Fact]
    public async Task MacKeychainSecretStore_NotFoundAndFailure_BehaveAsExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = new MacKeychainSecretStore(new FakeCommandRunner((_, _, _) => Result(44, error: "could not be found")));
        (await missing.GetAsync("k", ct)).Should().BeNull();
        await missing.RemoveAsync("k", ct);

        var broken = new MacKeychainSecretStore(new FakeCommandRunner((_, _, _) => Result(1, error: "User interaction is not allowed.")));
        var act = async () => await broken.SetAsync("k", "v", ct);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("User interaction");
    }

    /// <summary>
    /// Verifies the Linux store keeps the secret out of the arguments and passes it over standard input.
    /// </summary>
    [Fact]
    public async Task LibSecretStore_Operations_UseSecretToolWithStdin()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new FakeCommandRunner((_, args, _) => args[0] == "lookup" ? Result(0, "the-secret") : Result(0));
        var store = new LibSecretStore(runner);

        await store.SetAsync("account:1", "the-secret", ct);
        var value = await store.GetAsync("account:1", ct);
        await store.RemoveAsync("account:1", ct);

        value.Should().Be("the-secret");
        runner.Calls[0].Arguments.Should().Equal("store", "--label=Swiftlet", "service", "Swiftlet", "account", "account:1");
        runner.Calls[0].StandardInput.Should().Be("the-secret");
        runner.Calls[0].Arguments.Should().NotContain("the-secret");
        runner.Calls[1].Arguments.Should().Equal("lookup", "service", "Swiftlet", "account", "account:1");
        runner.Calls[2].Arguments.Should().Equal("clear", "service", "Swiftlet", "account", "account:1");
    }

    /// <summary>
    /// Verifies "not found" (exit 1, silent) is null while an error message is reported.
    /// </summary>
    [Fact]
    public async Task LibSecretStore_NotFoundAndFailure_BehaveAsExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        (await new LibSecretStore(new FakeCommandRunner((_, _, _) => Result(1))).GetAsync("k", ct)).Should().BeNull();

        var locked = new LibSecretStore(new FakeCommandRunner((_, _, _) => Result(1, error: "Cannot autolaunch D-Bus")));
        var act = async () => await locked.GetAsync("k", ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies availability detection: installed and working, missing tool, and no Secret Service.
    /// </summary>
    [Fact]
    public async Task LibSecretStore_IsAvailable_DetectsToolAndService()
    {
        var ct = TestContext.Current.CancellationToken;
        var working = new FakeCommandRunner((_, _, _) => Result(1));
        var noService = new FakeCommandRunner((_, _, _) => Result(1, error: "Cannot autolaunch D-Bus without X11"));
        var missing = new FakeCommandRunner((_, _, _) => throw new Win32Exception("No such file"));

        (await LibSecretStore.IsAvailableAsync(working, ct)).Should().BeTrue();
        (await LibSecretStore.IsAvailableAsync(noService, ct)).Should().BeFalse();
        (await LibSecretStore.IsAvailableAsync(missing, ct)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies the factory picks DPAPI on Windows and does not touch external tools there.
    /// </summary>
    [Fact]
    public void Create_OnCurrentPlatform_ReturnsPlatformStore()
    {
        var runner = new FakeCommandRunner((_, _, _) => Result(1));
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        try
        {
            var store = PlatformSecretStore.Create(directory, NullLogger.Instance, runner);

            if (OperatingSystem.IsWindows())
            {
                store.Should().BeOfType<WindowsSecretStore>();
                runner.Calls.Should().BeEmpty();
            }
            else if (OperatingSystem.IsMacOS())
            {
                store.Should().BeOfType<MacKeychainSecretStore>();
            }
            else
            {
                store.Should().BeOfType<LibSecretStore>();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
