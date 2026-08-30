import type {
  SessionSnapshot,
  TenantMembership,
  TenantRole,
} from '../../api/platform';

const administrativeRoles: readonly TenantRole[] = ['TenantOwner', 'TenantAdmin'];

export function activeMembership(
  session: SessionSnapshot | undefined,
): TenantMembership | undefined {
  if (!session) {
    return undefined;
  }

  return (
    session.memberships.find(
      (membership) => membership.tenantId === session.activeTenantId,
    ) ?? session.memberships[0]
  );
}

export function canAdminister(session: SessionSnapshot | undefined): boolean {
  if (!session) {
    return false;
  }

  if (session.user.platformAdmin) {
    return true;
  }

  const membership = activeMembership(session);
  return (
    membership?.status === 'active' &&
    administrativeRoles.includes(membership.role)
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
