using System.Security.Cryptography;
using System.Text;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Installation;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Update;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// An update service whose state the test sets.
/// </summary>
internal sealed class FakeUpdateService : IUpdateService
{
    /// <summary>
    /// A version to offer in tests.
    /// </summary>
    public static readonly AvailableVersion Newer =
        new("2.0.0", "- Faster sync", new Uri("https://github.com/coldhighsun/Swiftlet/releases/tag/v2.0.0"));

    /// <inheritdoc />
    public bool IsSupported { get; set; } = true;

    /// <inheritdoc />
    public UpdateStatus Status { get; set; }

    /// <inheritdoc />
    public AvailableVersion? Available { get; set; }

    /// <inheritdoc />
    public bool CanInstall { get; set; }

    /// <inheritdoc />
    public UpdateProgress? Progress { get; set; }

    /// <inheritdoc />
    public Exception? CheckError { get; set; }

    /// <inheritdoc />
    public Exception? InstallError { get; set; }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public event EventHandler? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler? Announced;

    /// <inheritdoc />
    public event EventHandler? InstallerStarted;

    /// <summary>
    /// Gets the number of checks asked for.
    /// </summary>
    public int Checks { get; private set; }

    /// <summary>
    /// Gets the number of times a version was skipped.
    /// </summary>
    public int Skips { get; private set; }

    /// <summary>
    /// Gets the token of the running automatic checks.
    /// </summary>
    public CancellationToken RunToken { get; private set; }

    /// <summary>
    /// Gets or sets what an installation does.
    /// </summary>
    public Func<CancellationToken, Task<bool>> Install { get; set; } = _ => Task.FromResult(true);

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        RunToken = cancellationToken;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> InstallAsync(CancellationToken cancellationToken)
    {
        return Install(cancellationToken);
    }

    /// <inheritdoc />
    public Task SkipAsync()
    {
        Skips++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Raises <see cref="Changed"/>.
    /// </summary>
    public void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raises <see cref="ProgressChanged"/>.
    /// </summary>
    public void RaiseProgress()
    {
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raises <see cref="Announced"/>.
    /// </summary>
    public void RaiseAnnounced()
    {
        Announced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raises <see cref="InstallerStarted"/>.
    /// </summary>
    public void RaiseInstallerStarted()
    {
        InstallerStarted?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Records the "new version" pop-ups.
/// </summary>
internal sealed class FakeUpdateNoticePresenter : IUpdateNoticePresenter
{
    /// <summary>
    /// Gets the open pop-up, if any.
    /// </summary>
    public UpdateNoticeViewModel? Shown { get; private set; }

    /// <inheritdoc />
    public void Show(UpdateNoticeViewModel viewModel)
    {
        Shown = viewModel;
    }

    /// <inheritdoc />
    public void Close()
    {
        Shown = null;
    }
}

/// <summary>
/// Serves one release from memory, with its checksum file.
/// </summary>
internal sealed class FakeReleaseClient : IGitHubReleaseClient
{
    /// <summary>
    /// The content of the installer.
    /// </summary>
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("installer");

    /// <summary>
    /// Gets or sets the tag of the latest release; <see langword="null"/> when there is none.
    /// </summary>
    public string? Tag { get; set; } = "v2.0.0";

    /// <summary>
    /// Gets or sets the page of the release.
    /// </summary>
    public string Page { get; set; } = "https://github.com/coldhighsun/Swiftlet/releases/tag/v2.0.0";

    /// <summary>
    /// Gets or sets an exception thrown by a check.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the checksum file lists a hash the installer does not have.
    /// </summary>
    public bool WrongChecksum { get; set; }

    /// <summary>
    /// Gets the number of checks.
    /// </summary>
    public int Checks { get; private set; }

    /// <summary>
    /// Gets the number of downloads.
    /// </summary>
    public int Downloads { get; private set; }

    /// <inheritdoc />
    public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        Checks++;
        if (Failure is not null)
        {
            return Task.FromException<GitHubRelease?>(Failure);
        }

        var release = Tag is null
            ? null
            : new GitHubRelease
            {
                TagName = Tag,
                Body = "- Faster sync",
                HtmlUrl = Page,
                Assets =
                [
                    new GitHubAsset { Id = 1, Name = $"Swiftlet-Setup-{Tag}.exe", Size = Installer.Length },
                    new GitHubAsset { Id = 2, Name = "SHA256SUMS.txt", Size = 100 },
                ],
            };
        return Task.FromResult(release);
    }

    /// <inheritdoc />
    public Task<GitHubRelease?> GetReleaseByTagAsync(
        string owner, string repo, string tag, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(
        string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        Downloads++;
        var stream = new MemoryStream(Installer);
        return Task.FromResult(new AssetStream(stream, Installer.Length, stream));
    }

    /// <inheritdoc />
    public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
    {
        var hash = WrongChecksum
            ? new string('0', 64)
            : Convert.ToHexStringLower(SHA256.HashData(Installer));
        return Task.FromResult($"{hash}  Swiftlet-Setup-{Tag}.exe\n");
    }
}

/// <summary>
/// Records the installers it was asked to start instead of starting them.
/// </summary>
internal sealed class FakeInstallerLauncher : IInstallerLauncher
{
    /// <summary>
    /// Gets the files and arguments of the starts.
    /// </summary>
    public List<(string Path, string? Arguments)> Launches { get; } = [];

    /// <inheritdoc />
    public InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options)
    {
        Launches.Add((installerPath, options.Arguments));
        return new InstallerLaunchResult(installerPath, null);
    }
}
