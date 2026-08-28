using Vistara.Domain.Common;

namespace Vistara.Domain.Identity;

public sealed class User
{
    private readonly List<ExternalIdentityLink> _externalIdentities = [];
    private readonly List<LocalIdentityLink> _localIdentities = [];

    private User(
        UserId id,
        NormalizedEmail email,
        string displayName,
        DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        Status = UserStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public UserId Id { get; }

    public NormalizedEmail Email { get; private set; }

    public string DisplayName { get; private set; }

    public UserStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<LocalIdentityLink> LocalIdentities => _localIdentities;

    public IReadOnlyList<ExternalIdentityLink> ExternalIdentities => _externalIdentities;

    public static Result<User> Create(
        UserId id,
        string email,
        string displayName,
        DateTimeOffset createdAt)
    {
        if (createdAt.Offset != TimeSpan.Zero)
        {
            return Result.Failure<User>(IdentityErrors.TimestampNotUtc);
        }

        Result<NormalizedEmail> emailResult = NormalizedEmail.Create(email);
        if (!emailResult.TryGetValue(out NormalizedEmail normalizedEmail))
        {
            return Result.Failure<User>(emailResult.Error!);
        }

        string normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        if (normalizedDisplayName.Length is < 1 or > 200)
        {
            return Result.Failure<User>(IdentityErrors.InvalidDisplayName);
        }

        return Result.Success(new User(id, normalizedEmail, normalizedDisplayName, createdAt));
    }

    public Result LinkLocalIdentity(
        LocalIdentityId identityId,
        string login,
        DateTimeOffset linkedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(linkedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        Result<NormalizedLogin> loginResult = NormalizedLogin.Create(login);
        if (!loginResult.TryGetValue(out NormalizedLogin normalizedLogin))
        {
            return Result.Failure(loginResult.Error!);
        }

        if (_localIdentities.Any(link =>
                link.Id == identityId ||
                link.Login == normalizedLogin))
        {
            return Result.Failure(IdentityErrors.LocalIdentityExists);
        }

        _localIdentities.Add(new LocalIdentityLink(identityId, Id, normalizedLogin, linkedAt));
        MarkChanged(linkedAt);
        return Result.Success();
    }

    public Result LinkExternalIdentity(
        ExternalIdentityId identityId,
        string issuer,
        string subject,
        DateTimeOffset linkedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(linkedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        Result<ExternalIssuer> issuerResult = ExternalIssuer.Create(issuer);
        if (!issuerResult.TryGetValue(out ExternalIssuer normalizedIssuer))
        {
            return Result.Failure(issuerResult.Error!);
        }

        string normalizedSubject = subject?.Trim() ?? string.Empty;
        if (normalizedSubject.Length is < 1 or > 512)
        {
            return Result.Failure(IdentityErrors.InvalidExternalSubject);
        }

        if (_externalIdentities.Any(link =>
                link.Id == identityId ||
                (link.Issuer == normalizedIssuer &&
                 string.Equals(link.Subject, normalizedSubject, StringComparison.Ordinal))))
        {
            return Result.Failure(IdentityErrors.ExternalIdentityExists);
        }

        _externalIdentities.Add(new ExternalIdentityLink(
            identityId,
            Id,
            normalizedIssuer,
            normalizedSubject,
            linkedAt));
        MarkChanged(linkedAt);
        return Result.Success();
    }

    public Result Suspend(DateTimeOffset changedAt) =>
        TransitionTo(UserStatus.Suspended, changedAt);

    public Result Activate(DateTimeOffset changedAt) =>
        TransitionTo(UserStatus.Active, changedAt);

    public Result Disable(DateTimeOffset changedAt) =>
        TransitionTo(UserStatus.Disabled, changedAt);

    private Result TransitionTo(UserStatus target, DateTimeOffset changedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(changedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        if (Status == target)
        {
            return Result.Failure(IdentityErrors.StatusUnchanged);
        }

        bool isAllowed = Status switch
        {
            UserStatus.Active => target is UserStatus.Suspended or UserStatus.Disabled,
            UserStatus.Suspended => target is UserStatus.Active or UserStatus.Disabled,
            UserStatus.Disabled => false,
            _ => false,
        };

        if (!isAllowed)
        {
            return Result.Failure(IdentityErrors.InvalidStatusTransition);
        }

        Status = target;
        MarkChanged(changedAt);
        return Result.Success();
    }

    private Result ValidateMutationTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            return Result.Failure(IdentityErrors.TimestampNotUtc);
        }

        return timestamp < UpdatedAt
            ? Result.Failure(IdentityErrors.TimestampOutOfOrder)
            : Result.Success();
    }

    private void MarkChanged(DateTimeOffset changedAt)
    {
        UpdatedAt = changedAt;
        Version++;
    }
}
