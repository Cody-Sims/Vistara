using System.Globalization;
using System.Text;
using Vistara.Domain.Common;

namespace Vistara.Domain.Gallery;

public sealed class TagCatalog
{
    private readonly List<Tag> _tags = [];

    public TagCatalog(GalleryTenantId tenantId)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Tenant identifier cannot be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public GalleryTenantId TenantId { get; }

    public long Version { get; private set; }

    public IReadOnlyList<Tag> Tags => _tags.AsReadOnly();

    public Result<Tag> CreateTag(TagId id, string displayName, string? color)
    {
        if (id.Value == Guid.Empty)
        {
            return Result.Failure<Tag>(GalleryErrors.InvalidIdentifier());
        }

        Result<TagName> nameResult = TagName.Create(displayName);
        if (!nameResult.TryGetValue(out TagName? name))
        {
            return Result.Failure<Tag>(nameResult.Error!);
        }

        if (_tags.Any(tag => tag.NormalizedName == name.Normalized))
        {
            return Result.Failure<Tag>(GalleryErrors.DuplicateTagName());
        }

        var tag = new Tag(id, TenantId, name.Display, name.Normalized, color);
        _tags.Add(tag);
        Version++;
        return Result.Success(tag);
    }

    public Result RenameTag(TagId id, string displayName, long expectedTagVersion)
    {
        Tag? tag = _tags.SingleOrDefault(candidate => candidate.Id == id);
        if (tag is null)
        {
            return Result.Failure(GalleryErrors.TagNotFound());
        }

        Result<TagName> nameResult = TagName.Create(displayName);
        if (!nameResult.TryGetValue(out TagName? name))
        {
            return Result.Failure(nameResult.Error!);
        }

        if (_tags.Any(candidate => candidate.Id != id && candidate.NormalizedName == name.Normalized))
        {
            return Result.Failure(GalleryErrors.DuplicateTagName());
        }

        Result renamed = tag.Rename(name, expectedTagVersion);
        if (renamed.IsSuccess)
        {
            Version++;
        }

        return renamed;
    }
}

public sealed class Tag
{
    internal Tag(
        TagId id,
        GalleryTenantId tenantId,
        string displayName,
        string normalizedName,
        string? color)
    {
        Id = id;
        TenantId = tenantId;
        DisplayName = displayName;
        NormalizedName = normalizedName;
        Color = color;
        Version = 1;
    }

    public TagId Id { get; }

    public GalleryTenantId TenantId { get; }

    public string DisplayName { get; private set; }

    public string NormalizedName { get; private set; }

    public string? Color { get; }

    public long Version { get; private set; }

    internal Result Rename(TagName name, long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            return Result.Failure(GalleryErrors.VersionConflict());
        }

        if (name.Normalized == NormalizedName && name.Display == DisplayName)
        {
            return Result.Success();
        }

        DisplayName = name.Display;
        NormalizedName = name.Normalized;
        Version++;
        return Result.Success();
    }
}

internal sealed record TagName(string Display, string Normalized)
{
    public static Result<TagName> Create(string value)
    {
        string display = GalleryText.CollapseWhitespace(value);
        if (display.Length == 0)
        {
            return Result.Failure<TagName>(GalleryErrors.TagNameRequired());
        }

        string normalized = display
            .Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);
        return Result.Success(new TagName(display.Normalize(NormalizationForm.FormKC), normalized));
    }
}

internal static class GalleryText
{
    public static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;

        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
