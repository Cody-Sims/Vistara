using Vistara.Domain.Common;

namespace Vistara.Domain.Lifecycle;

internal static class LifecycleErrors
{
    public static ResultError InvalidIdentifier() =>
        ResultError.Validation("lifecycle.identifier_invalid", "The identifier must not be empty.");

    public static ResultError TimestampMustBeUtc() =>
        ResultError.Validation("lifecycle.timestamp_not_utc", "The timestamp must be UTC.");

    public static ResultError HoldReasonRequired() =>
        ResultError.Validation("lifecycle.hold_reason_required", "A retention hold reason is required.");

    public static ResultError HoldReleaseBeforeCreation() =>
        ResultError.Validation("lifecycle.hold_release_before_creation", "A hold cannot be released before creation.");

    public static ResultError PurgeDeadlineInvalid() =>
        ResultError.Validation("lifecycle.purge_deadline_invalid", "The purge deadline must follow trash time.");

    public static ResultError TrashReasonRequired() =>
        ResultError.Validation("lifecycle.trash_reason_required", "A trash reason is required.");

    public static ResultError CrossTenantOrAssetReference() =>
        ResultError.Forbidden("lifecycle.cross_tenant_reference", "The hold belongs to another tenant or asset.");

    public static ResultError HoldNotFound() =>
        ResultError.NotFound("lifecycle.hold_not_found", "The retention hold was not found.");

    public static ResultError InvalidLifecycleTransition() =>
        ResultError.Conflict("lifecycle.transition_invalid", "The lifecycle transition is not allowed.");

    public static ResultError VersionConflict() =>
        ResultError.Conflict("lifecycle.version_conflict", "The lifecycle resource version has changed.");

    public static ResultError PurgeInitiatorInvalid() =>
        ResultError.Validation("lifecycle.purge_initiator_invalid", "The purge initiator is invalid.");

    public static ResultError PurgeBlocked() =>
        ResultError.Conflict("lifecycle.purge_blocked", "Retention, holds, revisions, or references block purge.");

    public static ResultError PurgeEvaluationStale() =>
        ResultError.Conflict(
            "lifecycle.purge_evaluation_stale",
            "The purge eligibility evaluation no longer matches the lifecycle state.");

    public static ResultError TombstoneBackupExpiryInvalid() =>
        ResultError.Validation(
            "lifecycle.tombstone_backup_expiry_invalid",
            "Backup expiry cannot precede purge time.");

    public static ResultError TombstoneRelationshipsInvalid() =>
        ResultError.Validation(
            "lifecycle.tombstone_relationships_invalid",
            "Tombstone relationship metadata is invalid.");

    public static ResultError InvalidPurgeBatchTransition() =>
        ResultError.Conflict("lifecycle.purge_batch_transition_invalid", "The purge batch transition is not allowed.");

    public static ResultError DryRunInvalid() =>
        ResultError.Validation("lifecycle.dry_run_invalid", "The purge dry-run metadata is invalid.");

    public static ResultError SeparatePurgeApproverRequired() =>
        ResultError.Forbidden("lifecycle.separate_approver_required", "A separate user must approve the purge.");

    public static ResultError PurgeBatchItemInvalid() =>
        ResultError.Validation("lifecycle.purge_batch_item_invalid", "The purge batch item is invalid.");

    public static ResultError PurgeBatchItemDuplicate() =>
        ResultError.Conflict("lifecycle.purge_batch_item_duplicate", "The asset already has a batch result.");
}

internal static class LifecycleTime
{
    public static bool IsUtc(DateTimeOffset timestamp) => timestamp.Offset == TimeSpan.Zero;
}
