using Microsoft.AspNetCore.Http;

namespace Vistara.Api.Features.Shares;

public enum ShareAccessDecisionStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public sealed record ShareAccessDecision
{
    private ShareAccessDecision(
        ShareAccessDecisionStatus status,
        Guid? tenantId,
        Guid? actorId)
    {
        Status = status;
        TenantId = tenantId;
        ActorId = actorId;
    }

    public ShareAccessDecisionStatus Status { get; }

    public Guid? TenantId { get; }

    public Guid? ActorId { get; }

    public static ShareAccessDecision Authorized(
        Guid tenantId,
        Guid actorId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        return new ShareAccessDecision(
            ShareAccessDecisionStatus.Authorized,
            tenantId,
            actorId);
    }

    public static ShareAccessDecision Denied(ShareAccessDecisionStatus status)
    {
        if (status == ShareAccessDecisionStatus.Authorized ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new ShareAccessDecision(status, null, null);
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Sharing access identities must use UUIDv7.",
                parameterName);
        }
    }
}

public interface IShareAuthorizationPort
{
    ValueTask<ShareAccessDecision> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken);
}
