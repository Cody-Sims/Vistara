using Vistara.Domain.Common;

namespace Vistara.Domain.Gallery;

internal static class GalleryErrors
{
    public static ResultError InvalidIdentifier() =>
        ResultError.Validation("gallery.identifier_invalid", "The identifier must not be empty.");

    public static ResultError AlbumNameRequired() =>
        ResultError.Validation("gallery.album_name_required", "The album name is required.");

    public static ResultError TagNameRequired() =>
        ResultError.Validation("gallery.tag_name_required", "The tag name is required.");

    public static ResultError DuplicateTagName() =>
        ResultError.Conflict("gallery.tag_name_duplicate", "An equivalent tag name already exists.");

    public static ResultError DuplicateAlbumItem() =>
        ResultError.Conflict("gallery.album_item_duplicate", "The asset is already in the album.");

    public static ResultError CrossTenantReference() =>
        ResultError.Forbidden("gallery.cross_tenant_reference", "The referenced resource belongs to another tenant.");

    public static ResultError TimestampMustBeUtc() =>
        ResultError.Validation("gallery.timestamp_not_utc", "The timestamp must be UTC.");

    public static ResultError InvalidAlbumPosition() =>
        ResultError.Validation("gallery.album_position_invalid", "The album position is outside the valid range.");

    public static ResultError AlbumItemNotFound() =>
        ResultError.NotFound("gallery.album_item_not_found", "The album item was not found.");

    public static ResultError TagNotFound() =>
        ResultError.NotFound("gallery.tag_not_found", "The tag was not found.");

    public static ResultError VersionConflict() =>
        ResultError.Conflict("gallery.version_conflict", "The gallery resource version has changed.");
}

internal static class GalleryTime
{
    public static bool IsUtc(DateTimeOffset timestamp) => timestamp.Offset == TimeSpan.Zero;
}
