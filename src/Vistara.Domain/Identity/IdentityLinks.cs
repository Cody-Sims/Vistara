namespace Vistara.Domain.Identity;

public sealed class LocalIdentityLink
{
    internal LocalIdentityLink(
        LocalIdentityId id,
        UserId userId,
        NormalizedLogin login,
        DateTimeOffset linkedAt)
    {
        Id = id;
        UserId = userId;
        Login = login;
        LinkedAt = linkedAt;
    }

    public LocalIdentityId Id { get; }

    public UserId UserId { get; }

    public NormalizedLogin Login { get; }

    public DateTimeOffset LinkedAt { get; }
}

public sealed class ExternalIdentityLink
{
    internal ExternalIdentityLink(
        ExternalIdentityId id,
        UserId userId,
        ExternalIssuer issuer,
        string subject,
        DateTimeOffset linkedAt)
    {
        Id = id;
        UserId = userId;
        Issuer = issuer;
        Subject = subject;
        LinkedAt = linkedAt;
    }

    public ExternalIdentityId Id { get; }

    public UserId UserId { get; }

    public ExternalIssuer Issuer { get; }

    public string Subject { get; }

    public DateTimeOffset LinkedAt { get; }
}
