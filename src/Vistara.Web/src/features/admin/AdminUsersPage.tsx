import { useCallback, useState, type FormEvent } from 'react';
import { isStaleVersion, isStateConflict, versionTag } from '../../api/versionTag';
import type {
  PlatformApiClient,
  TenantMember,
  TenantMemberCollection,
  TenantRole,
} from '../../api/platform';
import { describeRole } from '../session';
import {
  AdminEmpty,
  AdminFailure,
  AdminLoading,
  AdminPage,
} from './AdminPage';
import { formatMoment } from './format';
import styles from './admin.module.css';
import { useRemoteResource } from '../../app/useRemoteResource';

export type AdminUsersClient = Pick<
  PlatformApiClient,
  'listTenantMembers' | 'inviteTenantMember' | 'updateTenantMember'
>;

interface AdminUsersPageProps {
  readonly client: AdminUsersClient;
  /** The tenant the session is scoped to; members are never read across tenants. */
  readonly tenantId: string;
}

const invitableRoles: readonly TenantRole[] = [
  'TenantAdmin',
  'Member',
  'Viewer',
];

const allRoles: readonly TenantRole[] = [
  'TenantOwner',
  'TenantAdmin',
  'Member',
  'Viewer',
];

const statusLabels: Record<string, string> = {
  Active: 'Active',
  Invited: 'Invited',
  Suspended: 'Suspended',
  Removed: 'Removed',
};

export function AdminUsersPage({ client, tenantId }: AdminUsersPageProps) {
  const load = useCallback(
    () => client.listTenantMembers(tenantId),
    [client, tenantId],
  );
  const { state, reload, refresh } =
    useRemoteResource<TenantMemberCollection>(load);
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<TenantRole>('Member');
  const [inviting, setInviting] = useState(false);
  const [roleDrafts, setRoleDrafts] = useState<Record<string, TenantRole>>({});
  const [savingMember, setSavingMember] = useState<string>();
  const [confirmation, setConfirmation] = useState('');
  const [failure, setFailure] = useState('');

  async function invite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const address = email.trim();
    setConfirmation('');
    setFailure('');
    if (!address) {
      setFailure('Enter the email address to invite.');
      return;
    }

    setInviting(true);
    try {
      await client.inviteTenantMember(tenantId, { email: address, role });
      setEmail('');
      setConfirmation(`Invitation sent to ${address}.`);
      await refresh();
    } catch (error) {
      setFailure(
        isStateConflict(error)
          ? `${address} is already a member of this workspace.`
          : `The invitation for ${address} could not be sent. Nothing changed.`,
      );
    } finally {
      setInviting(false);
    }
  }

  async function saveRole(member: TenantMember) {
    const role = roleDrafts[member.userId] ?? member.role;
    setSavingMember(member.userId);
    setConfirmation('');
    setFailure('');

    try {
      await client.updateTenantMember(
        tenantId,
        member.userId,
        { role },
        { ifMatch: versionTag(member.version) },
      );
      setRoleDrafts((current) => {
        const next = { ...current };
        delete next[member.userId];
        return next;
      });
      setConfirmation(
        `${member.displayName} is now ${describeRole(role).toLowerCase()} of this workspace.`,
      );
      await refresh();
    } catch (error) {
      setFailure(
        isStaleVersion(error)
          ? `${member.displayName} changed somewhere else. Reload people before saving so nothing is overwritten.`
          : isStateConflict(error)
            ? `${member.displayName} cannot take that role right now. A workspace keeps at least one owner.`
            : `${member.displayName} could not be updated. Nothing changed.`,
      );
    } finally {
      setSavingMember(undefined);
    }
  }

  return (
    <AdminPage
      title="People"
      description="Everyone who can reach this workspace, and the invitations waiting to be accepted."
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
                <th scope="col">Role</th>
                <th scope="col">Membership</th>
                <th scope="col">Joined</th>
              </tr>
            </thead>
            <tbody>
              {state.value.items.map((person) => (
                <tr key={person.userId}>
                  <th scope="row">
                    <span className={styles.primaryCell}>
                      {person.displayName}
                    </span>
                    <span className={styles.secondaryCell}>{person.email}</span>
                  </th>
                  <td>
                    <div className={styles.rowActions}>
                      <select
                        aria-label={`Role for ${person.displayName}`}
                        className={styles.control}
                        value={roleDrafts[person.userId] ?? person.role}
                        onChange={(event) =>
                          setRoleDrafts((current) => ({
                            ...current,
                            [person.userId]: event.target.value as TenantRole,
                          }))
                        }
                      >
                        {allRoles.map((value) => (
                          <option key={value} value={value}>
                            {describeRole(value)}
                          </option>
                        ))}
                      </select>
                      <button
                        aria-label={`Save role for ${person.displayName}`}
                        className={styles.secondaryButton}
                        disabled={
                          (roleDrafts[person.userId] ?? person.role) ===
                            person.role || savingMember === person.userId
                        }
                        type="button"
                        onClick={() => void saveRole(person)}
                      >
                        {savingMember === person.userId ? 'Saving…' : 'Save'}
                      </button>
                    </div>
                  </td>
                  <td>
                    <span className={styles.badge} data-status={person.status}>
                      {statusLabels[person.status] ?? person.status}
                    </span>
                  </td>
                  <td>{formatMoment(person.joinedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      {state.kind === 'ready' ? (
        <form className={styles.form} onSubmit={(event) => void invite(event)}>
          <fieldset className={styles.fieldset}>
            <legend>Invite someone</legend>
            <div className={styles.field}>
              <label htmlFor="invite-email">Email address</label>
              <input
                autoComplete="off"
                className={styles.control}
                id="invite-email"
                name="email"
                type="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
              />
              <p className={styles.fieldHint}>
                The invitation is listed here until it is accepted.
              </p>
            </div>
            <div className={styles.field}>
              <label htmlFor="invite-role">Role</label>
              <select
                className={styles.control}
                id="invite-role"
                value={role}
                onChange={(event) =>
                  setRole(event.target.value as TenantRole)
                }
              >
                {invitableRoles.map((value) => (
                  <option key={value} value={value}>
                    {describeRole(value)}
                  </option>
                ))}
              </select>
            </div>
            <div className={styles.formActions}>
              <button
                className={styles.primaryButton}
                disabled={inviting}
                type="submit"
              >
                {inviting ? 'Sending…' : 'Send invitation'}
              </button>
            </div>
          </fieldset>
        </form>
      ) : null}

    </AdminPage>
  );
}
