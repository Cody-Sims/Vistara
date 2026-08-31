using Azure.Core;
using Azure.Identity;
using Npgsql;
using Vistara.Persistence;
using Vistara.Persistence.Azure;
using Xunit;

namespace Vistara.IntegrationTests.Persistence.Azure;

public sealed class VistaraNpgsqlDataSourceProviderTests
{
    private const string SecondConnectionString =
        "Host=vistara.postgres.database.azure.com;Port=5432;Database=vistara_jobs;" +
        "Username=vistara_worker_runtime;SSL Mode=VerifyFull;" +
        "GSS Encryption Mode=Disable;Include Error Detail=false";

    [Fact]
    public void Disabled_provider_never_creates_a_data_source()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            new PersistenceAzureOptions());

        Assert.False(provider.IsEnabled);
        Assert.Null(provider.GetDataSource(AzureEntraTestSupport.AzureConnectionString));
    }

    [Fact]
    public void Same_connection_string_reuses_one_data_source()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential());

        NpgsqlDataSource? first =
            provider.GetDataSource(AzureEntraTestSupport.AzureConnectionString);
        NpgsqlDataSource? second =
            provider.GetDataSource(AzureEntraTestSupport.AzureConnectionString);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Distinct_connection_strings_get_distinct_data_sources()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential());

        NpgsqlDataSource? first =
            provider.GetDataSource(AzureEntraTestSupport.AzureConnectionString);
        NpgsqlDataSource? second = provider.GetDataSource(SecondConnectionString);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Concurrent_callers_share_one_data_source()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential());

        NpgsqlDataSource?[] dataSources =
        [
            .. Enumerable.Range(0, 32)
                .AsParallel()
                .Select(_ => provider.GetDataSource(
                    AzureEntraTestSupport.AzureConnectionString)),
        ];

        Assert.Single(dataSources.Distinct());
    }

    [Fact]
    public void One_managed_identity_credential_backs_every_data_source()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions());

        TokenCredential credential = provider.Credential;

        Assert.IsType<ManagedIdentityCredential>(credential);
        Assert.Same(credential, provider.Credential);
    }

    [Fact]
    public void Disabled_provider_exposes_no_credential()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            new PersistenceAzureOptions());

        Assert.Throws<InvalidOperationException>(() => provider.Credential);
    }

    [Fact]
    public void Invalid_options_fail_closed_before_a_credential_exists()
    {
        Assert.Throws<InvalidOperationException>(
            () => new VistaraNpgsqlDataSourceProvider(new PersistenceAzureOptions
            {
                EntraTokenEnabled = true,
                ManagedIdentityClientId = "not-a-guid",
            }));
    }

    [Fact]
    public void Disposal_releases_every_data_source()
    {
        var provider = new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential());
        NpgsqlDataSource dataSource = Assert.IsAssignableFrom<NpgsqlDataSource>(
            provider.GetDataSource(AzureEntraTestSupport.AzureConnectionString));

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => dataSource.OpenConnection());
        Assert.Throws<ObjectDisposedException>(
            () => provider.GetDataSource(AzureEntraTestSupport.AzureConnectionString));
    }

    [Fact]
    public void Sqlite_deployments_may_not_enable_entra_tokens()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential());

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => provider.EnsureSupports(VistaraDatabaseProvider.Sqlite));

        Assert.Contains("SQLite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sqlite_deployments_are_untouched_when_entra_tokens_are_off()
    {
        using var provider = new VistaraNpgsqlDataSourceProvider(
            new PersistenceAzureOptions());

        provider.EnsureSupports(VistaraDatabaseProvider.Sqlite);
    }
}
