using Vistara.Domain.Common;

namespace Vistara.Domain.Tenancy;

public sealed class Tenant
{
    private Tenant(TenantId id, TenantSlug slug, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Slug = slug;
        Name = name;
        Status = TenantStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public TenantId Id { get; }

    public TenantSlug Slug { get; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Result<Tenant> Create(
        TenantId id,
        string slug,
        string name,
        DateTimeOffset createdAt)
    {
        Result timestampResult = ValidateUtc(createdAt);
        if (timestampResult.IsFailure)
        {
            return Result.Failure<Tenant>(timestampResult.Error!);
        }

        Result<TenantSlug> slugResult = TenantSlug.Create(slug);
        if (!slugResult.TryGetValue(out TenantSlug normalizedSlug))
        {
            return Result.Failure<Tenant>(slugResult.Error!);
        }

        string normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > 200)
        {
            return Result.Failure<Tenant>(TenancyErrors.InvalidName);
        }

        return Result.Success(new Tenant(id, normalizedSlug, normalizedName, createdAt));
    }

    public Result Rename(string name, DateTimeOffset changedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(changedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        string normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > 200)
        {
            return Result.Failure(TenancyErrors.InvalidName);
        }

        if (string.Equals(Name, normalizedName, StringComparison.Ordinal))
        {
            return Result.Failure(ResultError.Conflict(
                "tenancy.name_unchanged",
                "The tenant already has the requested name."));
        }

        Name = normalizedName;
        MarkChanged(changedAt);
        return Result.Success();
    }

    public Result Suspend(DateTimeOffset changedAt) =>
        TransitionTo(TenantStatus.Suspended, changedAt);

    public Result Activate(DateTimeOffset changedAt) =>
        TransitionTo(TenantStatus.Active, changedAt);

    public Result Deactivate(DateTimeOffset changedAt) =>
        TransitionTo(TenantStatus.Deactivated, changedAt);

    private Result TransitionTo(TenantStatus target, DateTimeOffset changedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(changedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        if (Status == target)
        {
            return Result.Failure(TenancyErrors.StatusUnchanged);
        }

        bool isAllowed = Status switch
        {
            TenantStatus.Active => target is TenantStatus.Suspended or TenantStatus.Deactivated,
            TenantStatus.Suspended => target is TenantStatus.Active or TenantStatus.Deactivated,
            TenantStatus.Deactivated => false,
            _ => false,
        };

        if (!isAllowed)
        {
            return Result.Failure(TenancyErrors.InvalidStatusTransition);
        }

        Status = target;
        MarkChanged(changedAt);
        return Result.Success();
    }

    private Result ValidateMutationTimestamp(DateTimeOffset timestamp)
    {
        Result utcResult = ValidateUtc(timestamp);
        if (utcResult.IsFailure)
        {
            return utcResult;
        }

        return timestamp < UpdatedAt
            ? Result.Failure(TenancyErrors.TimestampOutOfOrder)
            : Result.Success();
    }

    private static Result ValidateUtc(DateTimeOffset timestamp) =>
        timestamp.Offset != TimeSpan.Zero
            ? Result.Failure(TenancyErrors.TimestampNotUtc)
            : Result.Success();

    private void MarkChanged(DateTimeOffset changedAt)
    {
        UpdatedAt = changedAt;
        Version++;
    }
}
