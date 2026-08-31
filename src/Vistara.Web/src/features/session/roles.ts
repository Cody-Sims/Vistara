import type {
  CurrentUser,
  TenantMembership,
  TenantRole,
} from '../../api/platform';

const administrativeRoles: readonly TenantRole[] = [
  'TenantOwner',
  'TenantAdmin',
];

/**
 * The membership the session is scoped to. A session without `tenantId`, or
 * one whose tenant is not in the membership list, has no active membership:
 * another tenant's role never stands in for it.
 */
export function activeMembership(
  user: CurrentUser | undefined,
): TenantMembership | undefined {
  if (!user?.tenantId) {
    return undefined;
  }

  return user.tenants.find(
    (membership) => membership.id === user.tenantId,
  );
}

export function canAdminister(user: CurrentUser | undefined): boolean {
  const membership = activeMembership(user);

  return (
    membership !== undefined &&
    membership.membershipStatus === 'Active' &&
    administrativeRoles.includes(membership.role) &&
    (user?.role === undefined || administrativeRoles.includes(user.role))
  );
}

export function describeRole(role: TenantRole | undefined): string {
  switch (role) {
    case 'TenantOwner':
      return 'Owner';
    case 'TenantAdmin':
      return 'Administrator';
    case 'Member':
      return 'Member';
    case 'Viewer':
      return 'Viewer';
    default:
      return 'Guest';
  }
}
