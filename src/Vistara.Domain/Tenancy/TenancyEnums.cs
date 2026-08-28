namespace Vistara.Domain.Tenancy;

public enum TenantStatus
{
    Active,
    Suspended,
    Deactivated,
}

public enum TenantRole
{
    TenantOwner,
    TenantAdmin,
    Member,
    Viewer,
}

public enum MembershipStatus
{
    Invited,
    Active,
    Suspended,
    Removed,
}
