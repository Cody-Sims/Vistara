using Vistara.Domain.Common;
using Vistara.Domain.Lifecycle;

namespace Vistara.UnitTests.Lifecycle;

public sealed class LifecycleDomainTests
{
    private static readonly LifecycleTenantId Tenant = new(Guid.Parse("0195c111-1111-7111-8111-111111111111"));
    private static readonly LifecycleUserId Actor = new(Guid.Parse("0195c222-2222-7222-8222-222222222222"));
    private static readonly LifecycleAssetId AssetId = new(Guid.Parse("0195c333-3333-7333-8333-333333333333"));
    private static readonly DateTimeOffset TrashedAt = new(2030, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void Trash_and_restore_are_idempotent_and_preserve_relationships()
    {
        AssetLifecycle lifecycle = AssetLifecycle.Create(AssetId, Tenant, currentRevision: 7);
        RelationshipSnapshot relationships = RelationshipSnapshot.Create(
        [
            new(RelationshipKind.Album, Guid.Parse("0195c444-4444-7444-8444-444444444444")),
            new(RelationshipKind.Tag, Guid.Parse("0195c555-5555-7555-8555-555555555555")),
            new(RelationshipKind.Favorite, Actor.Value),
            new(RelationshipKind.Share, Guid.Parse("0195c666-6666-7666-8666-666666666666")),
        ]);

        Assert.True(lifecycle.Trash(Actor, TrashedAt, TrashedAt.AddDays(30), "cleanup", relationships).IsSuccess);
        long trashedVersion = lifecycle.Version;
        Assert.True(lifecycle.Trash(Actor, TrashedAt, TrashedAt.AddDays(30), "cleanup", relationships).IsSuccess);
        Assert.Equal(trashedVersion, lifecycle.Version);

        Result<RelationshipSnapshot> restored = lifecycle.Restore(Actor, TrashedAt.AddDays(2));
        RelationshipSnapshot restoredRelationships = Value(restored);
        long restoredVersion = lifecycle.Version;
        Assert.Equal(relationships, restoredRelationships);
        Assert.Equal(Actor, lifecycle.LastRestoration?.RestoredBy);
        Assert.Equal(TrashedAt.AddDays(2), lifecycle.LastRestoration?.RestoredAtUtc);
        Assert.True(lifecycle.Restore(Actor, TrashedAt.AddDays(3)).IsSuccess);
        Assert.Equal(restoredVersion, lifecycle.Version);
        Assert.Equal(AssetLifecycleState.Ready, lifecycle.State);
    }

    [Fact]
    public void Retention_holds_deadlines_revisions_and_references_block_purge()
    {
        AssetLifecycle lifecycle = TrashedLifecycle();
        RetentionHold hold = Value(RetentionHold.Create(
            new RetentionHoldId(Guid.Parse("0195c777-7777-7777-8777-777777777777")),
            Tenant,
            AssetId,
            "legal",
            Actor,
            TrashedAt.AddDays(1)));
        Assert.True(lifecycle.PlaceHold(hold).IsSuccess);

        PurgeEligibility held = lifecycle.EvaluatePurge(
            new PurgeEvaluation(TrashedAt.AddDays(31), 7, false));
        Assert.Contains(PurgeBarrier.ActiveHold, held.Barriers);

        Assert.True(lifecycle.ReleaseHold(hold.Id, Actor, TrashedAt.AddDays(2)).IsSuccess);
        Assert.Contains(
            PurgeBarrier.RetentionPeriod,
            lifecycle.EvaluatePurge(new PurgeEvaluation(TrashedAt.AddDays(29), 7, false)).Barriers);
        Assert.Contains(
            PurgeBarrier.RevisionChanged,
            lifecycle.EvaluatePurge(new PurgeEvaluation(TrashedAt.AddDays(31), 8, false)).Barriers);
        Assert.Contains(
            PurgeBarrier.BlockingReference,
            lifecycle.EvaluatePurge(new PurgeEvaluation(TrashedAt.AddDays(31), 7, true)).Barriers);
        Assert.True(
            lifecycle.EvaluatePurge(new PurgeEvaluation(TrashedAt.AddDays(31), 7, false)).IsEligible);
    }

    [Fact]
    public void Permanent_purge_requires_explicit_begin_and_complete_transitions()
    {
        AssetLifecycle lifecycle = TrashedLifecycle();
        var evaluation = new PurgeEvaluation(TrashedAt.AddDays(31), 7, false);
        var request = new PurgeRequest(
            new PurgeBatchId(Guid.Parse("0195c888-8888-7888-8888-888888888888")),
            Actor,
            PurgeInitiatorKind.Human,
            evaluation);

        Assert.True(lifecycle.BeginPurge(request, lifecycle.Version).IsSuccess);
        Assert.Equal(AssetLifecycleState.Purging, lifecycle.State);
        Assert.True(lifecycle.Restore(Actor, TrashedAt.AddDays(31)).IsFailure);

        PurgeExecutionToken executionToken = Value(
            lifecycle.EvaluatePurgeForExecution(
                new PurgeEvaluation(TrashedAt.AddDays(32), 7, false),
                lifecycle.Version));
        Assert.Equal(AssetId, executionToken.AssetId);
        Assert.Equal(Tenant, executionToken.TenantId);
        Assert.Equal(7, executionToken.ObservedRevision);
        Assert.Equal(lifecycle.Version, executionToken.LifecycleVersion);
        Assert.Equal(AssetLifecycleState.Purging, executionToken.State);
        Assert.Equal(TrashedAt.AddDays(30), executionToken.RetentionPurgeAtUtc);
        Assert.Equal(0, executionToken.ActiveHoldCount);
        Assert.False(executionToken.HasBlockingReferences);
        Assert.Equal(
            lifecycle.Relationships.Digest,
            executionToken.RelationshipDigest);
        Result<DeletionTombstone> completed = lifecycle.CompletePurge(
            executionToken,
            TrashedAt.AddDays(32),
            TrashedAt.AddDays(120));
        DeletionTombstone tombstone = Value(completed);

        Assert.Equal(AssetLifecycleState.Purged, lifecycle.State);
        Assert.Equal(AssetId, tombstone.FormerAssetId);
        Assert.Equal(Tenant, tombstone.TenantId);
        Assert.Equal(4, tombstone.RelationshipCount);
        Assert.False(string.IsNullOrWhiteSpace(tombstone.RelationshipDigest));
        Assert.True(lifecycle.Restore(Actor, TrashedAt.AddDays(33)).IsFailure);
    }

    [Fact]
    public void Hold_placed_after_purge_begins_prevents_completion()
    {
        AssetLifecycle lifecycle = TrashedLifecycle();
        var evaluation = new PurgeEvaluation(TrashedAt.AddDays(31), 7, false);
        var request = new PurgeRequest(
            new PurgeBatchId(Guid.Parse("0195c888-8888-7888-8888-888888888888")),
            Actor,
            PurgeInitiatorKind.Human,
            evaluation);
        Assert.True(lifecycle.BeginPurge(request, lifecycle.Version).IsSuccess);
        PurgeExecutionToken executionToken = Value(
            lifecycle.EvaluatePurgeForExecution(evaluation, lifecycle.Version));

        RetentionHold hold = Value(RetentionHold.Create(
            new RetentionHoldId(Guid.Parse("0195c777-7777-7777-8777-777777777777")),
            Tenant,
            AssetId,
            "legal",
            Actor,
            TrashedAt.AddDays(31)));
        Assert.True(lifecycle.PlaceHold(hold).IsSuccess);

        Result<DeletionTombstone> completed = lifecycle.CompletePurge(
            executionToken,
            TrashedAt.AddDays(32),
            TrashedAt.AddDays(120));

        Assert.Equal("lifecycle.purge_evaluation_stale", completed.Error?.Code);
        Assert.Equal(
            "lifecycle.purge_blocked",
            lifecycle.EvaluatePurgeForExecution(
                new PurgeEvaluation(TrashedAt.AddDays(32), 7, false),
                lifecycle.Version).Error?.Code);
        Assert.Equal(AssetLifecycleState.Purging, lifecycle.State);
    }

    [Fact]
    public void Purge_completion_rejects_stale_eligibility_after_lifecycle_version_changes()
    {
        AssetLifecycle lifecycle = TrashedLifecycle();
        var evaluation = new PurgeEvaluation(TrashedAt.AddDays(31), 7, false);
        var request = new PurgeRequest(
            new PurgeBatchId(Guid.Parse("0195c888-8888-7888-8888-888888888888")),
            Actor,
            PurgeInitiatorKind.Human,
            evaluation);
        Assert.True(lifecycle.BeginPurge(request, lifecycle.Version).IsSuccess);
        PurgeExecutionToken executionToken = Value(
            lifecycle.EvaluatePurgeForExecution(evaluation, lifecycle.Version));

        RetentionHold hold = Value(RetentionHold.Create(
            new RetentionHoldId(Guid.Parse("0195c777-7777-7777-8777-777777777777")),
            Tenant,
            AssetId,
            "temporary review",
            Actor,
            TrashedAt.AddDays(31)));
        Assert.True(lifecycle.PlaceHold(hold).IsSuccess);
        Assert.True(lifecycle.ReleaseHold(
            hold.Id,
            Actor,
            TrashedAt.AddDays(31).AddMinutes(1)).IsSuccess);

        Result<DeletionTombstone> completed = lifecycle.CompletePurge(
            executionToken,
            TrashedAt.AddDays(32),
            TrashedAt.AddDays(120));

        Assert.Equal("lifecycle.purge_evaluation_stale", completed.Error?.Code);
        Assert.Equal(AssetLifecycleState.Purging, lifecycle.State);
    }

    [Fact]
    public void Purge_execution_rechecks_revision_and_blocking_references()
    {
        AssetLifecycle lifecycle = TrashedLifecycle();
        var request = new PurgeRequest(
            new PurgeBatchId(Guid.Parse("0195c888-8888-7888-8888-888888888888")),
            Actor,
            PurgeInitiatorKind.Human,
            new PurgeEvaluation(TrashedAt.AddDays(31), 7, false));
        Assert.True(lifecycle.BeginPurge(request, lifecycle.Version).IsSuccess);

        Result<PurgeExecutionToken> staleRevision =
            lifecycle.EvaluatePurgeForExecution(
                new PurgeEvaluation(TrashedAt.AddDays(31), 8, false),
                lifecycle.Version);
        Result<PurgeExecutionToken> referenced =
            lifecycle.EvaluatePurgeForExecution(
                new PurgeEvaluation(TrashedAt.AddDays(31), 7, true),
                lifecycle.Version);

        Assert.Equal("lifecycle.purge_blocked", staleRevision.Error?.Code);
        Assert.Equal("lifecycle.purge_blocked", referenced.Error?.Code);
    }

    [Fact]
    public void Lifecycle_identifiers_require_non_empty_uuid7_values()
    {
        Action<Guid>[] constructors =
        [
            value => _ = new LifecycleTenantId(value),
            value => _ = new LifecycleUserId(value),
            value => _ = new LifecycleAssetId(value),
            value => _ = new RetentionHoldId(value),
            value => _ = new PurgeBatchId(value),
        ];
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        foreach (Action<Guid> construct in constructors)
        {
            Assert.Throws<ArgumentException>(() => construct(Guid.Empty));
            Assert.Throws<ArgumentException>(() => construct(versionFour));
        }
    }

    [Fact]
    public void Tombstones_reject_backup_expiry_before_purge_time()
    {
        Result<DeletionTombstone> result = DeletionTombstone.Create(
            AssetId,
            Tenant,
            TrashedAt.AddDays(40),
            TrashedAt.AddDays(39),
            relationshipCount: 0,
            relationshipDigest: RelationshipSnapshot.Empty.Digest);

        Assert.Equal("lifecycle.tombstone_backup_expiry_invalid", result.Error?.Code);
    }

    [Fact]
    public void Tombstones_require_a_sha256_relationship_digest()
    {
        Result<DeletionTombstone> result = DeletionTombstone.Create(
            AssetId,
            Tenant,
            TrashedAt.AddDays(40),
            TrashedAt.AddDays(41),
            relationshipCount: 0,
            relationshipDigest: "not-a-digest");

        Assert.Equal("lifecycle.tombstone_relationships_invalid", result.Error?.Code);
    }

    [Fact]
    public void Purge_batch_enforces_dry_run_approval_execution_and_completion_order()
    {
        PurgeBatch batch = PurgeBatch.Create(
            new PurgeBatchId(Guid.Parse("0195c999-9999-7999-8999-999999999999")),
            Tenant,
            Actor,
            TrashedAt);

        Assert.True(batch.Start(TrashedAt.AddMinutes(1), batch.Version).IsFailure);
        Assert.True(batch.RecordDryRun("sha256:dry-run", 2, 1, TrashedAt.AddMinutes(2), batch.Version).IsSuccess);
        Assert.Equal(TrashedAt.AddMinutes(2), batch.DryRunCompletedAtUtc);
        Assert.True(batch.Approve(
            new LifecycleUserId(Guid.Parse("0195caaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa")),
            TrashedAt.AddMinutes(3),
            batch.Version).IsSuccess);
        Assert.True(batch.Start(TrashedAt.AddMinutes(4), batch.Version).IsSuccess);
        Assert.True(batch.RecordItem(
            new PurgeBatchItem(AssetId, 7, PurgeItemResult.Purged, 4096),
            batch.Version).IsSuccess);
        Assert.True(batch.Complete(TrashedAt.AddMinutes(5), batch.Version).IsSuccess);

        Assert.Equal(PurgeBatchState.Completed, batch.State);
        Assert.Equal(1, batch.ProcessedCount);
        Assert.Equal(4096, batch.ReclaimedBytes);
    }

    private static AssetLifecycle TrashedLifecycle()
    {
        AssetLifecycle lifecycle = AssetLifecycle.Create(AssetId, Tenant, currentRevision: 7);
        RelationshipSnapshot relationships = RelationshipSnapshot.Create(
        [
            new(RelationshipKind.Album, Guid.Parse("0195cb11-1111-7111-8111-111111111111")),
            new(RelationshipKind.Tag, Guid.Parse("0195cb22-2222-7222-8222-222222222222")),
            new(RelationshipKind.Favorite, Actor.Value),
            new(RelationshipKind.Share, Guid.Parse("0195cb33-3333-7333-8333-333333333333")),
        ]);
        Assert.True(lifecycle.Trash(
            Actor,
            TrashedAt,
            TrashedAt.AddDays(30),
            "cleanup",
            relationships).IsSuccess);
        return lifecycle;
    }

    private static T Value<T>(Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Message);
        return value;
    }
}
