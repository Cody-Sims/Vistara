namespace Vistara.Domain.Identity;

public enum UserStatus
{
    Active,
    Suspended,
    Disabled,
}

public enum SessionStatus
{
    Active,
    Expired,
    Revoked,
}

public enum ApiKeyStatus
{
    Active,
    Expired,
    Revoked,
}

[Flags]
public enum ApiKeyScope
{
    None = 0,
    ReadAssets = 1 << 0,
    UploadAssets = 1 << 1,
    ManageMetadata = 1 << 2,
    ManageApiKeys = 1 << 3,
}
