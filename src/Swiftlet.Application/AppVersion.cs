using System.Reflection;

namespace Swiftlet.Application;

/// <summary>
/// The version of the application. The build takes it from the git tags (MinVer) and gives it to every assembly.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Gets the full version, including the build metadata (usually the commit hash).
    /// </summary>
    public static string Full { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

    /// <summary>
    /// Gets the version without the build metadata, for showing to people and servers.
    /// </summary>
    public static string Display { get; } = WithoutMetadata(Full);

    /// <summary>
    /// Removes the build metadata the build appends to a version.
    /// </summary>
    /// <param name="version">A semantic version.</param>
    /// <returns>The version up to the first <c>+</c>.</returns>
    public static string WithoutMetadata(string version)
    {
        return version.Split('+')[0];
    }
}
