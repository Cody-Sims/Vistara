using Microsoft.Extensions.Configuration;
using Vistara.Persistence.Azure;
using Xunit;

namespace Vistara.IntegrationTests.Persistence.Azure;

public sealed class PersistenceAzureOptionsTests
{
    private const string ClientId = "8d5c2f1e-0f2b-4a3d-9a1b-2c3d4e5f6071";

    [Fact]
    public void Missing_section_leaves_entra_tokens_disabled()
    {
        PersistenceAzureOptions options =
            PersistenceAzureOptions.FromConfiguration(
                Configuration(new Dictionary<string, string?>()));

        Assert.False(options.EntraTokenEnabled);
        Assert.Null(options.ManagedIdentityClientId);
    }

    [Fact]
    public void Absent_configuration_leaves_entra_tokens_disabled()
    {
        PersistenceAzureOptions options =
            PersistenceAzureOptions.FromConfiguration(null);

        Assert.False(options.EntraTokenEnabled);
    }

    [Fact]
    public void Enabled_section_binds_every_documented_key()
    {
        PersistenceAzureOptions options = PersistenceAzureOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>
            {
                ["Persistence:Azure:EntraTokenEnabled"] = "true",
                ["Persistence:Azure:ManagedIdentityClientId"] = ClientId,
                ["Persistence:Azure:TokenRefreshInterval"] = "00:30:00",
                ["Persistence:Azure:TokenRetryInterval"] = "00:00:10",
                ["Persistence:Azure:TokenScope"] =
                    "https://ossrdbms-aad.database.usgovcloudapi.net/.default",
            }));

        Assert.True(options.EntraTokenEnabled);
        Assert.Equal(ClientId, options.ManagedIdentityClientId);
        Assert.Equal(TimeSpan.FromMinutes(30), options.TokenRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.TokenRetryInterval);
        Assert.Equal(
            "https://ossrdbms-aad.database.usgovcloudapi.net/.default",
            options.TokenScope);
    }

    [Fact]
    public void Enabled_section_applies_the_documented_defaults()
    {
        PersistenceAzureOptions options = PersistenceAzureOptions.FromConfiguration(
            Configuration(new Dictionary<string, string?>
            {
                ["Persistence:Azure:EntraTokenEnabled"] = "true",
                ["Persistence:Azure:ManagedIdentityClientId"] = ClientId,
            }));

        Assert.Equal(TimeSpan.FromMinutes(55), options.TokenRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.TokenRetryInterval);
        Assert.Equal(
            "https://ossrdbms-aad.database.windows.net/.default",
            options.TokenScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Enabled_mode_requires_a_user_assigned_client_id(string? clientId)
    {
        var options = new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = clientId,
        };

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ManagedIdentityClientId", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ossrdbms-aad.database.windows.net/.default")]
    [InlineData("http://ossrdbms-aad.database.windows.net/.default")]
    [InlineData("https://ossrdbms-aad.database.windows.net/")]
    [InlineData("https://ossrdbms-aad.database.windows.net/user_impersonation")]
    public void Enabled_mode_requires_an_https_default_scope(string scope)
    {
        var options = new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = ClientId,
            TokenScope = scope,
        };

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TokenScope", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-00:01:00")]
    [InlineData("00:00:00")]
    [InlineData("00:00:30")]
    [InlineData("01:00:01")]
    [InlineData("12:00:00")]
    public void Refresh_interval_stays_inside_the_entra_token_lifetime(string interval)
    {
        var options = new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = ClientId,
            TokenRefreshInterval = TimeSpan.Parse(interval, null),
        };

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TokenRefreshInterval", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-00:00:05")]
    [InlineData("00:00:00")]
    [InlineData("00:10:00")]
    public void Retry_interval_must_be_positive_and_short(string interval)
    {
        var options = new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = ClientId,
            TokenRetryInterval = TimeSpan.Parse(interval, null),
        };

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TokenRetryInterval", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_interval_may_not_exceed_the_refresh_interval()
    {
        var options = new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = ClientId,
            TokenRefreshInterval = TimeSpan.FromMinutes(2),
            TokenRetryInterval = TimeSpan.FromMinutes(3),
        };

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TokenRetryInterval", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_but_disabled_section_fails_closed()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => PersistenceAzureOptions.FromConfiguration(
                Configuration(new Dictionary<string, string?>
                {
                    ["Persistence:Azure:ManagedIdentityClientId"] = ClientId,
                })));

        Assert.Contains("EntraTokenEnabled", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_enabled_options_pass_validation()
    {
        var options = new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = ClientId,
        };

        options.Validate();
    }

    private static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
