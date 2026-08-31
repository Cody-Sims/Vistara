import { AdminPage, AdminPendingContract } from './AdminPage';

/**
 * The audit log is specified but has no route yet, so this page reads nothing
 * and says so rather than inventing a request.
 */
export function AdminAuditPage() {
  return (
    <AdminPage
      title="Audit log"
      description="Privileged actions are recorded on the server. The gallery cannot show them until the audit route is published."
    >
      <AdminPendingContract
        title="Audit events"
        description="Reading the log needs a tenant-scoped, filterable, cursor-paged route. Entries stay read-only: the gallery will never edit or delete them."
        contract={
          'GET /api/v1/admin/audit?outcome=…&action=…&limit=…&cursor=… → { items: [{ id, occurredAt, actor { kind, id, displayName }, action, outcome, resourceType, resourceId, requestId }], nextCursor? }'
        }
      />
    </AdminPage>
  );
}
