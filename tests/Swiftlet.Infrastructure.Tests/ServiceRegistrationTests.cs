using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the dependency injection registration of the infrastructure layer.
/// </summary>
public sealed class ServiceRegistrationTests : IDisposable
{
    /// <summary>
    /// The data directory of the test.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// Verifies the database contexts get their connection strings from the object that prepared the databases.
    /// </summary>
    [Fact]
    public async Task AddInfrastructure_Resolved_SharesOneProtectionAsConnectionStrings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_directory.Root);

        // Replaces the platform store so the test never touches a real keychain.
        services.AddSingleton<ISecretStore>(new InMemorySecretStore());
        await using var provider = services.BuildServiceProvider();
        var protection = provider.GetRequiredService<IDatabaseProtection>();

        provider.GetRequiredService<IDatabaseConnectionStrings>().Should().BeSameAs(protection);
        await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<GlobalDbContext>().Database.GetConnectionString().Should().Contain("Password=");
        protection.Status.Should().Be(DatabaseProtectionStatus.Encrypted);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }
}
