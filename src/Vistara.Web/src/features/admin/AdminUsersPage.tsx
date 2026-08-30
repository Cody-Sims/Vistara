import { useCallback, useState } from 'react';
import { VistaraApiError } from '../../api/generated/client';
import type {
  AdminUser,
  AdminUserPage,
  MembershipStatus,
  PlatformApiClient,
  TenantRole,
} from '../../api/platform';
import {
  AdminEmpty,
  AdminFailure,
  AdminLoading,
  AdminPage,
} from './AdminPage';
import { formatMoment } from './format';
import { useAdminResource } from './useAdminResource';
import styles from './admin.module.css';

export type AdminUsersClient = Pick<
  PlatformApiClient,
  'listAdminUsers' | 'updateAdminUser'
>;

interface AdminUsersPageProps {
  readonly client: AdminUsersClient;
}

const roles: readonly { value: TenantRole; label: string }[] = [
  { value: 'TenantOwner', label: 'Owner' },
  { value: 'TenantAdmin', label: 'Administrator' },
  { value: 'Member', label: 'Member' },
  { value: 'Viewer', label: 'Viewer' },
];

const statusLabels: Record<MembershipStatus, string> = {
  active: 'Active',
  invited: 'Invited',
  suspended: 'Suspended',
};

const roleArticles: Record<TenantRole, string> = {
  TenantOwner: 'an owner',
  TenantAdmin: 'an administrator',
  Member: 'a member',
  Viewer: 'a viewer',
};

export function AdminUsersPage({ client }: AdminUsersPageProps) {
  const load = useCallback(
    () => client.listAdminUsers({ limit: 100 }),
    [client],
  );
  const { state, reload, refresh } = useAdminResource<AdminUserPage>(load);
  const [drafts, setDrafts] = useState<Record<string, TenantRole>>({});
  const [saving, setSaving] = useState<string>();
  const [confirmation, setConfirmation] = useState('');
  const [failure, setFailure] = useState('');

  async function save(person: AdminUser) {
    const role = drafts[person.id] ?? person.role;
    setSaving(person.id);
    setConfirmation('');
    setFailure('');

    try {
      await client.updateAdminUser(
        person.id,
        { role },
        { ifMatch: `"${person.version}"` },
      );
      setDrafts((current) => {
        const next = { ...current };
        delete next[person.id];
        return next;
      });
      setConfirmation(
        `${person.displayName} is now ${roleArticles[role]} of this workspace.`,
      );
      await refresh();
    } catch (error) {
      setFailure(
        error instanceof VistaraApiError && error.status === 409
          ? `${person.displayName} changed somewhere else. Reload people before saving so nothing is overwritten.`
          : `${person.displayName} could not be updated. Try again in a moment.`,
      );
    } finally {
      setSaving(undefined);
    }
  }

  return (
    <AdminPage
      title="People"
      description="Review who can reach this workspace and adjust what each person may do. Changes apply the next time they act."
    >
      <p className={styles.announce} role="status" aria-live="polite">
        {state.kind === 'loading' ? 'Loading people…' : confirmation}
      </p>

      {state.kind === 'loading' ? (
        <AdminLoading label="Loading people…" />
      ) : null}

      {state.kind === 'failed' ? (
        <AdminFailure
          title="People are unavailable"
          description="The member list could not be read. Nothing was changed."
          onRetry={reload}
        />
      ) : null}

      {failure ? (
        <p className={styles.alert} role="alert">
          {failure}
        </p>
      ) : null}

      {state.kind === 'ready' && state.value.items.length === 0 ? (
        <AdminEmpty>
          No one else has joined this workspace yet. Invitations appear here as
          soon as they are sent.
        </AdminEmpty>
      ) : null}

      {state.kind === 'ready' && state.value.items.length > 0 ? (
        <div className={styles.tableScroll}>
          <table className={styles.table}>
            <caption className={styles.caption}>
              {state.value.items.length} people in this workspace
            </caption>
            <thead>
              <tr>
                <th scope="col">Person</th>
                <th scope="col">Membership</th>
                <th scope="col">Last seen</th>
                <th scope="col">Role</th>
              </tr>
            </thead>
            <tbody>
              {state.value.items.map((person) => {
                const role = drafts[person.id] ?? person.role;
                const changed = role !== person.role;

                return (
                  <tr key={person.id}>
                    <th scope="row">
                      <span className={styles.primaryCell}>
                        {person.displayName}
                      </span>
                      <span className={styles.secondaryCell}>
                        {person.email}
                      </span>
                    </th>
                    <td>
                      <span
                        className={styles.badge}
                        data-status={person.status}
                      >
                        {statusLabels[person.status]}
                      </span>
                    </td>
                    <td>{formatMoment(person.lastSeenAt)}</td>
                    <td>
                      <div className={styles.rowActions}>
                        <select
                          aria-label={`Role for ${person.displayName}`}
                          className={styles.control}
                          value={role}
                          onChange={(event) =>
                            setDrafts((current) => ({
                              ...current,
                              [person.id]: event.target.value as TenantRole,
                            }))
                          }
                        >
                          {roles.map((option) => (
                            <option key={option.value} value={option.value}>
                              {option.label}
                            </option>
                          ))}
                        </select>
                        <button
                          aria-label={`Save role for ${person.displayName}`}
                          className={styles.secondaryButton}
                          disabled={!changed || saving === person.id}
                          type="button"
                          onClick={() => void save(person)}
                        >
                          {saving === person.id ? 'Saving…' : 'Save'}
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : null}
    </AdminPage>
  );
}
