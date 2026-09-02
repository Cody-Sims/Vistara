using Npgsql;
using Vistara.Persistence.Azure;
using Xunit;

namespace Vistara.IntegrationTests.Persistence.Azure;

public sealed class VistaraNpgsqlDataSourceFactoryTests
{
    [Fact]
    public async Task Token_provider_requests_the_configured_scope()
    {
        var credential = new RecordingTokenCredential();
        Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>> provider =
            VistaraNpgsqlDataSourceFactory.CreateTokenProvider(
                AzureEntraTestSupport.EnabledOptions(),
                credential);

        string token = await provider(
            new NpgsqlConnectionStringBuilder(AzureEntraTestSupport.AzureConnectionString),
            CancellationToken.None);

        Assert.Equal("token-1", token);
        Assert.Equal(
            ["https://ossrdbms-aad.database.windows.net/.default"],
            Assert.Single(credential.RequestedScopes));
    }

    [Fact]
    public async Task Token_provider_fetches_a_fresh_token_on_every_refresh()
    {
        var credential = new RecordingTokenCredential();
        Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>> provider =
            VistaraNpgsqlDataSourceFactory.CreateTokenProvider(
                AzureEntraTestSupport.EnabledOptions(),
                credential);
        var settings = new NpgsqlConnectionStringBuilder(
            AzureEntraTestSupport.AzureConnectionString);

        string first = await provider(settings, CancellationToken.None);
        string second = await provider(settings, CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task Token_provider_honors_cancellation()
    {
        var credential = new RecordingTokenCredential();
        Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>> provider =
            VistaraNpgsqlDataSourceFactory.CreateTokenProvider(
                AzureEntraTestSupport.EnabledOptions(),
                credential);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider(
                new NpgsqlConnectionStringBuilder(AzureEntraTestSupport.AzureConnectionString),
                cancellation.Token));

        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public async Task Token_provider_propagates_credential_failures()
    {
        var failure = new InvalidOperationException("managed identity unavailable");
        Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>> provider =
            VistaraNpgsqlDataSourceFactory.CreateTokenProvider(
                AzureEntraTestSupport.EnabledOptions(),
                new FailingTokenCredential(failure));

        InvalidOperationException thrown =
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider(
                    new NpgsqlConnectionStringBuilder(
                        AzureEntraTestSupport.AzureConnectionString),
                    CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void Created_data_source_never_exposes_the_token()
    {
        using NpgsqlDataSource dataSource = VistaraNpgsqlDataSourceFactory.Create(
            AzureEntraTestSupport.AzureConnectionString,
            AzureEntraTestSupport.EnabledOptions(),
            new RecordingTokenCredential());

        Assert.DoesNotContain(
            "Password",
            dataSource.ConnectionString,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;Username=api;" +
        "Password=leaked;SSL Mode=VerifyFull;GSS Encryption Mode=Disable",
        "Password")]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;Username=api;" +
        "Passfile=/run/secrets/pgpass;SSL Mode=VerifyFull;GSS Encryption Mode=Disable",
        "Passfile")]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;Username=api;" +
        "SSL Mode=Prefer;GSS Encryption Mode=Disable",
        "SSL Mode")]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;Username=api;" +
        "SSL Mode=Require;GSS Encryption Mode=Disable",
        "SSL Mode")]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;Username=api;" +
        "SSL Mode=VerifyFull",
        "GSS Encryption Mode")]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;Username=api;" +
        "SSL Mode=VerifyFull;GSS Encryption Mode=Prefer",
        "GSS Encryption Mode")]
    [InlineData(
        "Host=vistara.postgres.database.azure.com;Database=vistara;" +
        "SSL Mode=VerifyFull;GSS Encryption Mode=Disable",
        "Username")]
    [InlineData(
        "Database=vistara;Username=api;SSL Mode=VerifyFull;GSS Encryption Mode=Disable",
        "Host")]
    public void Unsafe_connection_strings_fail_closed(
        string connectionString,
        string expectedKeyword)
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => VistaraNpgsqlDataSourceFactory.Create(
                connectionString,
                AzureEntraTestSupport.EnabledOptions(),
                new RecordingTokenCredential()));

        Assert.Contains(expectedKeyword, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejection_messages_never_echo_the_connection_string()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => VistaraNpgsqlDataSourceFactory.Create(
                AzureEntraTestSupport.PasswordConnectionString,
                AzureEntraTestSupport.EnabledOptions(),
                new RecordingTokenCredential()));

        Assert.DoesNotContain(
            "local-development-password",
            failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_connection_strings_fail_closed()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => VistaraNpgsqlDataSourceFactory.Create(
                "Host=localhost;Not A Keyword=1",
                AzureEntraTestSupport.EnabledOptions(),
                new RecordingTokenCredential()));

        Assert.Contains("connection string", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_options_may_not_build_a_data_source()
    {
        Assert.Throws<InvalidOperationException>(
            () => VistaraNpgsqlDataSourceFactory.Create(
                AzureEntraTestSupport.AzureConnectionString,
                new PersistenceAzureOptions(),
                new RecordingTokenCredential()));
    }
}
