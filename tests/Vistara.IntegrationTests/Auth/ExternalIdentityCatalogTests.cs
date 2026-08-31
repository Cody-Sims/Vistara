using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vistara.Api.Features.Account;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Model;
using Vistara.Persistence.Tenancy;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Covers the tenant-independent external identity lookup an OIDC callback
/// needs before a tenant scope exists. The key is the provider issuer, which
/// carries both the provider and its identity-provider tenant, plus the
/// immutable subject (the Entra <c>oid</c>). Email is profile data and never
/// participates in resolution.
/// </summary>
public sealed class ExternalIdentityCatalogTests
{
    private const string EntraTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private const string OtherEntraTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string Issuer =
        $"https://login.microsoftonline.com/{EntraTenantId}/v2.0";
    private const string OtherTenantIssuer =
        $"https://login.microsoftonline.com/{OtherEntraTenantId}/v2.0";
    private const string OtherProviderIssuer = "https://accounts.example.com/v2.0";
    private const string ObjectId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string OtherObjectId = "5b8c1f22-0c58-4f1a-9d2a-6b9d6c1c9a10";

    [Fact]
    public async Task Exact_issuer_and_subject_resolve_the_linked_principal()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);

        ExternalIdentityLookupResult? resolved = await LookupAsync(
            harness,
            Issuer,
            ObjectId);

        Assert.NotNull(resolved);
        Assert.Equal(owner.UserId, resolved.UserId);
        Assert.Equal("owner@example.com", resolved.Email);
        Assert.Equal("Owner", resolved.DisplayName);
        Assert.False(resolved.IsDisabled);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_normalized_before_matching()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);

        ExternalIdentityLookupResult? resolved = await LookupAsync(
            harness,
            $"  {Issuer}\t",
            $" {ObjectId} ");

        Assert.NotNull(resolved);
        Assert.Equal(owner.UserId, resolved.UserId);
    }

    [Fact]
    public async Task Another_identity_provider_tenant_does_not_resolve()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);

        Assert.Null(await LookupAsync(harness, OtherTenantIssuer, ObjectId));
        Assert.Null(await LookupAsync(harness, OtherProviderIssuer, ObjectId));
        Assert.Null(await LookupAsync(harness, Issuer, OtherObjectId));
    }

    [Fact]
    public async Task Matching_is_exact_and_never_case_folded_or_prefixed()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);

        Assert.Null(await LookupAsync(harness, Issuer, ObjectId.ToUpperInvariant()));
        Assert.Null(await LookupAsync(harness, Issuer.ToUpperInvariant(), ObjectId));
        Assert.Null(await LookupAsync(harness, Issuer, ObjectId[..8]));
        Assert.Null(await LookupAsync(harness, Issuer, $"{ObjectId}x"));
        Assert.Null(await LookupAsync(harness, Issuer, "%"));
        Assert.Null(await LookupAsync(harness, $"{Issuer}/", ObjectId));
    }

    [Fact]
    public async Task A_verified_email_alone_never_resolves_a_principal()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Assert.Null(await LookupAsync(harness, Issuer, "owner@example.com"));
        Assert.Null(await LookupAsync(harness, "owner@example.com", ObjectId));
        Assert.Null(await LookupAsync(harness, Issuer, ObjectId));

        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);
        ExternalIdentityLookupResult? resolved =
            await LookupAsync(harness, Issuer, ObjectId);
        Assert.Equal(owner.UserId, resolved!.UserId);
    }

    [Fact]
    public async Task A_suspended_principal_is_reported_disabled()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);
        await SetStatusAsync(harness, owner.UserId, "Suspended");

        ExternalIdentityLookupResult? resolved =
            await LookupAsync(harness, Issuer, ObjectId);

        Assert.NotNull(resolved);
        Assert.True(resolved.IsDisabled);
    }

    [Fact]
    public async Task Duplicate_links_fail_closed()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);
        Guid intruder = await AddUserAsync(harness, "intruder@example.com");
        await DropUniqueIndexAsync(harness);
        await LinkAsync(harness, intruder, Issuer, ObjectId);

        Assert.Null(await LookupAsync(harness, Issuer, ObjectId));
    }

    [Fact]
    public async Task An_orphaned_link_fails_closed()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        _ = await harness.ProvisionAsync();
        await InsertOrphanLinkAsync(harness, Issuer, ObjectId);

        Assert.Null(await LookupAsync(harness, Issuer, ObjectId));
    }

    [Fact]
    public async Task Values_beyond_the_stored_bounds_fail_closed_without_a_query()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);
        var recorder = new CommandRecorder();

        await using IdentityCatalogDbContext context = CreateContext(harness, recorder);
        var catalog = new RelationalIdentityCatalog(context);

        Assert.Null(await catalog.FindByExternalIdentityAsync(
            new string('i', 2049),
            ObjectId,
            default));
        Assert.Null(await catalog.FindByExternalIdentityAsync(
            Issuer,
            new string('s', 513),
            default));
        Assert.Null(await catalog.FindByExternalIdentityAsync(
            Issuer,
            "line\nbreak",
            default));
        Assert.Empty(recorder.Commands);
    }

    [Fact]
    public async Task Blank_arguments_are_rejected()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        _ = await harness.ProvisionAsync();
        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        var catalog = new RelationalIdentityCatalog(context);

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await catalog.FindByExternalIdentityAsync(" ", ObjectId, default));
        _ = await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await catalog.FindByExternalIdentityAsync(Issuer, "", default));
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_lookup()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var recorder = new CommandRecorder();

        await using IdentityCatalogDbContext context = CreateContext(harness, recorder);
        var catalog = new RelationalIdentityCatalog(context);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await catalog.FindByExternalIdentityAsync(
                Issuer,
                ObjectId,
                cancelled.Token));
        Assert.Empty(recorder.Commands);
    }

    [Fact]
    public async Task The_lookup_runs_exactly_one_query()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);
        var recorder = new CommandRecorder();

        await using IdentityCatalogDbContext context = CreateContext(harness, recorder);
        var catalog = new RelationalIdentityCatalog(context);
        ExternalIdentityLookupResult? resolved =
            await catalog.FindByExternalIdentityAsync(Issuer, ObjectId, default);

        Assert.Equal(owner.UserId, resolved!.UserId);
        string sql = Assert.Single(recorder.Commands);
        Assert.Contains("external_identities", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountSelects(sql));
    }

    [Fact]
    public void The_lookup_translates_to_one_bounded_postgresql_query()
    {
        var options = new DbContextOptionsBuilder<IdentityCatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=vistara_identity;Username=unused")
            .Options;
        using var context = new IdentityCatalogDbContext(options);

        string sql = RelationalIdentityCatalog
            .ExternalIdentityQuery(context, Issuer, ObjectId)
            .Take(2)
            .ToQueryString();
        string body = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

        Assert.Equal(1, CountSelects(body));
        Assert.Contains("external_identities", body, StringComparison.Ordinal);
        Assert.Contains("issuer = @", body, StringComparison.Ordinal);
        Assert.Contains("subject = @", body, StringComparison.Ordinal);
        Assert.Contains("LIMIT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lower(", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Issuer, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ObjectId, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memberships_of_a_resolved_principal_match_the_browser_projection()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();
        await LinkAsync(harness, owner.UserId, Issuer, ObjectId);

        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        var catalog = new RelationalIdentityCatalog(context);
        ExternalIdentityLookupResult? resolved =
            await catalog.FindByExternalIdentityAsync(Issuer, ObjectId, default);
        IReadOnlyList<PersistedTenantMembership> memberships =
            await catalog.ListMembershipsAsync(resolved!.UserId, default);
        PersistedIdentitySummary? summary =
            await catalog.FindSummaryAsync(resolved.UserId, default);

        PersistedTenantMembership membership = Assert.Single(memberships);
        Assert.Equal(owner.TenantId, membership.TenantId);
        Assert.Equal(owner.TenantSlug, membership.Slug);
        Assert.Equal("TenantOwner", membership.Role);
        Assert.Equal("Active", membership.MembershipStatus);
        Assert.Equal("Active", membership.TenantStatus);
        Assert.NotNull(membership.JoinedAtUtc);
        Assert.Equal(summary!.Email, resolved.Email);
        Assert.Equal(summary.DisplayName, resolved.DisplayName);
    }

    [Fact]
    public async Task A_principal_without_a_membership_enumerates_nothing()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        _ = await harness.ProvisionAsync();
        Guid stranger = await AddUserAsync(harness, "stranger@example.com");
        await LinkAsync(harness, stranger, Issuer, OtherObjectId);

        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        var catalog = new RelationalIdentityCatalog(context);
        ExternalIdentityLookupResult? resolved =
            await catalog.FindByExternalIdentityAsync(Issuer, OtherObjectId, default);

        Assert.Equal(stranger, resolved!.UserId);
        Assert.Empty(await catalog.ListMembershipsAsync(resolved.UserId, default));
    }

    private static IdentityCatalogDbContext CreateContext(
        AccountSurfaceHarness harness,
        CommandRecorder recorder) =>
        new(new DbContextOptionsBuilder<IdentityCatalogDbContext>()
            .UseSqlite(harness.ConnectionString)
            .AddInterceptors(recorder)
            .Options);

    private static async Task<ExternalIdentityLookupResult?> LookupAsync(
        AccountSurfaceHarness harness,
        string issuer,
        string subject)
    {
        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        return await new RelationalIdentityCatalog(context)
            .FindByExternalIdentityAsync(issuer, subject, default);
    }

    private static async Task LinkAsync(
        AccountSurfaceHarness harness,
        Guid userId,
        string issuer,
        string subject)
    {
        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        _ = context.ExternalIdentities.Add(new ExternalIdentityRow
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Issuer = issuer,
            Subject = subject,
            LinkedAtUtc = DateTimeOffset.UtcNow,
        });
        _ = await context.SaveChangesAsync(default);
    }

    private static async Task<Guid> AddUserAsync(
        AccountSurfaceHarness harness,
        string email)
    {
        Guid userId = Guid.CreateVersion7();
        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        _ = context.Users.Add(new UserRow
        {
            Id = userId,
            NormalizedEmail = email,
            DisplayName = email,
            Status = "Active",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1,
        });
        _ = await context.SaveChangesAsync(default);
        return userId;
    }

    private static async Task SetStatusAsync(
        AccountSurfaceHarness harness,
        Guid userId,
        string status)
    {
        await using IdentityCatalogDbContext context = harness.CreateCatalog();
        UserRow row = await context.Users.SingleAsync(
            user => user.Id == userId,
            default);
        row.Status = status;
        _ = await context.SaveChangesAsync(default);
    }

    private static async Task DropUniqueIndexAsync(AccountSurfaceHarness harness) =>
        await ExecuteAsync(
            harness,
            "DROP INDEX IF EXISTS \"IX_external_identities_issuer_subject\";");

    private static async Task InsertOrphanLinkAsync(
        AccountSurfaceHarness harness,
        string issuer,
        string subject) =>
        await ExecuteAsync(
            harness,
            "INSERT INTO external_identities " +
            "(id, user_id, issuer, subject, linked_at_utc) VALUES " +
            $"('{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}', " +
            $"'{issuer}', '{subject}', '2026-08-31 00:00:00');",
            foreignKeys: false);

    private static async Task ExecuteAsync(
        AccountSurfaceHarness harness,
        string sql,
        bool foreignKeys = true)
    {
        string connectionString = foreignKeys
            ? harness.ConnectionString
            : $"{harness.ConnectionString};Foreign Keys=False";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(default);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(default);
    }

    private static int CountSelects(string sql)
    {
        int count = 0;
        int index = sql.IndexOf("SELECT", StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = sql.IndexOf("SELECT", index + 1, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Records every command the catalog actually sends.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands => _commands;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }
}
