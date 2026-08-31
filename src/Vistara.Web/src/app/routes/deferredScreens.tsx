import {
  AdminAuditPage,
  AdminJobsPage,
  AdminPoliciesPage,
  AdminStoragePage,
  AdminUsersPage,
} from '../../features/admin';
import {
  RequireAdministration,
  RequireSession,
  useSession,
} from '../../features/session';
import { SetupPage } from '../../features/session/SetupPage';
import { SettingsPage } from '../../features/settings';
import { platformClient } from '../apiClients';

/**
 * Screens only a signed-in operator opens. They live in their own module so a
 * visitor who lands on sign-in, first-run setup, or a public share never
 * downloads the administration and cloud setup code.
 */

export function AdminUsersScreen() {
  return (
    <RequireAdministration>
      <AdminUsersTenant />
    </RequireAdministration>
  );
}

function AdminUsersTenant() {
  const { user } = useSession();
  return user?.tenantId ? (
    <AdminUsersPage client={platformClient} tenantId={user.tenantId} />
  ) : (
    <p role="status">This session is not scoped to a workspace.</p>
  );
}

export function AdminStorageScreen() {
  return (
    <RequireAdministration>
      <AdminStoragePage client={platformClient} />
    </RequireAdministration>
  );
}

export function AdminJobsScreen() {
  return (
    <RequireAdministration>
      <AdminJobsPage client={platformClient} />
    </RequireAdministration>
  );
}

export function AdminPoliciesScreen() {
  return (
    <RequireAdministration>
      <AdminPoliciesPage client={platformClient} />
    </RequireAdministration>
  );
}

export function AdminAuditScreen() {
  return (
    <RequireAdministration>
      <AdminAuditPage />
    </RequireAdministration>
  );
}

export function SettingsScreen() {
  return (
    <RequireSession>
      <SettingsPage client={platformClient} />
    </RequireSession>
  );
}

export function SetupScreen() {
  return <SetupPage client={platformClient} />;
}
