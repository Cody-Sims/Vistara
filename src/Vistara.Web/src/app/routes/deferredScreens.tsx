import {
  AdminAuditPage,
  AdminJobsPage,
  AdminPoliciesPage,
  AdminStoragePage,
  AdminUsersPage,
} from '../../features/admin';
import { useSession } from '../../features/session';
import { SetupPage } from '../../features/session/SetupPage';
import { SettingsPage } from '../../features/settings';
import { platformClient } from '../apiClients';

/**
 * Screens only a signed-in operator opens. They live in their own module so a
 * visitor who lands on sign-in, first-run setup, or a public share never
 * downloads the administration and cloud setup code. The session and
 * administration guards stay in the route table, so an account that is denied
 * never fetches this module at all.
 */

export function AdminUsersScreen() {
  const { user } = useSession();
  return user?.tenantId ? (
    <AdminUsersPage client={platformClient} tenantId={user.tenantId} />
  ) : (
    <p role="status">This session is not scoped to a workspace.</p>
  );
}

export function AdminStorageScreen() {
  return <AdminStoragePage client={platformClient} />;
}

export function AdminJobsScreen() {
  return <AdminJobsPage client={platformClient} />;
}

export function AdminPoliciesScreen() {
  return <AdminPoliciesPage client={platformClient} />;
}

export function AdminAuditScreen() {
  return <AdminAuditPage />;
}

export function SettingsScreen() {
  return <SettingsPage client={platformClient} />;
}

export function SetupScreen() {
  return <SetupPage client={platformClient} />;
}
