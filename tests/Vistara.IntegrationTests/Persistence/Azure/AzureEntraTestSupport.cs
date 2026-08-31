using Azure.Core;
using Vistara.Persistence.Azure;

namespace Vistara.IntegrationTests.Persistence.Azure;

internal static class AzureEntraTestSupport
{
    internal const string ClientId = "8d5c2f1e-0f2b-4a3d-9a1b-2c3d4e5f6071";

    internal const string AzureConnectionString =
        "Host=vistara.postgres.database.azure.com;Port=5432;Database=vistara;" +
        "Username=vistara_api_runtime;SSL Mode=VerifyFull;" +
        "GSS Encryption Mode=Disable;Include Error Detail=false";

    internal const string PasswordConnectionString =
        "Host=localhost;Port=5432;Database=vistara;Username=vistara;" +
        "Password=local-development-password;SSL Mode=Disable";

    internal static PersistenceAzureOptions EnabledOptions()
    {
        return new PersistenceAzureOptions
        {
            EntraTokenEnabled = true,
            ManagedIdentityClientId = ClientId,
        };
    }
}

/// <summary>
/// Records every token request so tests can assert scope, refresh, cancellation,
/// and failure behavior without contacting the managed-identity endpoint.
/// </summary>
internal sealed class RecordingTokenCredential : TokenCredential
{
    private readonly Func<int, string> _tokenFactory;
    private readonly List<string[]> _requestedScopes = [];
    private readonly Lock _gate = new();
    private int _calls;

    internal RecordingTokenCredential(Func<int, string>? tokenFactory = null)
    {
        _tokenFactory = tokenFactory ?? (call => $"token-{call}");
    }

    internal int Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    internal IReadOnlyList<string[]> RequestedScopes
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestedScopes];
            }
        }
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int call;
        lock (_gate)
        {
            _requestedScopes.Add(requestContext.Scopes);
            call = ++_calls;
        }

        return new AccessToken(
            _tokenFactory(call),
            DateTimeOffset.UtcNow.AddMinutes(60));
    }

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

internal sealed class FailingTokenCredential(Exception failure) : TokenCredential
{
    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        throw failure;
    }

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        throw failure;
    }
}
