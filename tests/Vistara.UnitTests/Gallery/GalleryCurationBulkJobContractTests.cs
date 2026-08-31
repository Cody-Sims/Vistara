using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Curation;
using Vistara.Domain.Jobs;
using Xunit;

namespace Vistara.UnitTests.Gallery;

public sealed class GalleryCurationBulkJobContractTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ActorId = Guid.CreateVersion7();
    private static readonly Guid AssetId = Guid.CreateVersion7();
    private static readonly Guid TagId = Guid.CreateVersion7();

    [Fact]
    public void Bulk_payload_round_trips_the_tenant_actor_action_and_targets()
    {
        var payload = new GalleryCurationBulkJobPayload(
            TenantId,
            ActorId,
            actorCanManageAll: true,
            new BulkCurationAction("addTag", TagId, null, null),
            [new BulkCurationTarget(AssetId, 4)]);

        Assert.True(GalleryCurationJobContracts.TryParseBulk(
            GalleryCurationJobContracts.BulkType,
            GalleryCurationJobContracts.PayloadVersion,
            GalleryCurationJobContracts.SerializeBulk(payload),
            out GalleryCurationBulkJobPayload? parsed));

        Assert.Equal(TenantId, parsed!.TenantId);
        Assert.Equal(ActorId, parsed.ActorId);
        Assert.True(parsed.ActorCanManageAll);
        Assert.Equal(payload.Action, parsed.Action);
        Assert.Equal(payload.Items, parsed.Items);
        CurationActor actor = parsed.CreateActor();
        Assert.Equal(TenantId, actor.TenantId);
        Assert.Equal(ActorId, actor.UserId);
        Assert.True(actor.CanManageAll);
    }

    [Fact]
    public void Bulk_payload_rejects_another_job_type_or_payload_version()
    {
        string json = GalleryCurationJobContracts.SerializeBulk(Payload());

        Assert.False(GalleryCurationJobContracts.TryParseBulk(
            new JobType("lifecycle.restore"),
            GalleryCurationJobContracts.PayloadVersion,
            json,
            out GalleryCurationBulkJobPayload? byType));
        Assert.False(GalleryCurationJobContracts.TryParseBulk(
            GalleryCurationJobContracts.BulkType,
            1,
            json,
            out GalleryCurationBulkJobPayload? byVersion));

        Assert.Null(byType);
        Assert.Null(byVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"tenantId":"not-a-guid"}""")]
    [InlineData("""
        {"tenantId":"00000000-0000-0000-0000-000000000000",
         "actorId":"00000000-0000-0000-0000-000000000000",
         "actorCanManageAll":false,
         "action":{"kind":"setFavorite","tagId":null,"albumId":null,"favorite":true},
         "items":[]}
        """)]
    public void Bulk_payload_rejects_malformed_json(string json)
    {
        Assert.False(GalleryCurationJobContracts.TryParseBulk(
            GalleryCurationJobContracts.BulkType,
            GalleryCurationJobContracts.PayloadVersion,
            json,
            out GalleryCurationBulkJobPayload? payload));
        Assert.Null(payload);
    }

    [Fact]
    public void Bulk_payload_rejects_unknown_members()
    {
        string json = """
            {"tenantId":"019927c0-0000-7000-8000-000000000001",
             "actorId":"019927c0-0000-7000-8000-000000000002",
             "actorCanManageAll":false,
             "action":{"kind":"setFavorite","tagId":null,"albumId":null,"favorite":true},
             "items":[{"assetId":"019927c0-0000-7000-8000-000000000003","version":1}],
             "escalate":true}
            """;

        Assert.False(GalleryCurationJobContracts.TryParseBulk(
            GalleryCurationJobContracts.BulkType,
            GalleryCurationJobContracts.PayloadVersion,
            json,
            out GalleryCurationBulkJobPayload? payload));
        Assert.Null(payload);
    }

    [Fact]
    public void Bulk_payload_rejects_unsupported_or_ambiguous_actions()
    {
        Assert.Throws<ArgumentException>(() => Payload(
            action: new BulkCurationAction("trash", null, null, null)));
        Assert.Throws<ArgumentException>(() => Payload(
            action: new BulkCurationAction("setFavorite", TagId, null, true)));
        Assert.Throws<ArgumentException>(() => Payload(
            action: new BulkCurationAction("addTag", Guid.Empty, null, null)));
        Assert.Throws<ArgumentException>(() => Payload(
            action: new BulkCurationAction(
                "addToAlbum",
                null,
                Guid.NewGuid(),
                null)));
    }

    [Fact]
    public void Bulk_payload_rejects_unbounded_duplicate_or_unversioned_targets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Payload(items: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Payload(
            items: Enumerable
                .Range(0, 201)
                .Select(_ => new BulkCurationTarget(Guid.CreateVersion7(), 1))
                .ToArray()));
        Assert.Throws<ArgumentException>(() => Payload(
            items:
            [
                new BulkCurationTarget(AssetId, 1),
                new BulkCurationTarget(AssetId, 2),
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Payload(
            items: [new BulkCurationTarget(AssetId, 0)]));
        Assert.Throws<ArgumentException>(() => Payload(
            items: [new BulkCurationTarget(Guid.NewGuid(), 1)]));
    }

    [Fact]
    public void Bulk_payload_accepts_the_maximum_supported_batch()
    {
        GalleryCurationBulkJobPayload payload = Payload(
            items: Enumerable
                .Range(0, 200)
                .Select(_ => new BulkCurationTarget(Guid.CreateVersion7(), 1))
                .ToArray());

        Assert.Equal(200, payload.Items.Count);
    }

    private static GalleryCurationBulkJobPayload Payload(
        BulkCurationAction? action = null,
        IReadOnlyList<BulkCurationTarget>? items = null) =>
        new(
            TenantId,
            ActorId,
            actorCanManageAll: false,
            action ?? new BulkCurationAction("setFavorite", null, null, true),
            items ?? [new BulkCurationTarget(AssetId, 1)]);
}
