namespace Vistara.Persistence.Jobs;

public static class PostgreSqlJobClaimSql
{
    public const string Statement =
        """
        SELECT *
        FROM jobs
        WHERE (
                (
                    state IN ('Pending', 'RetryScheduled')
                    AND available_at_utc <= {0}
                    AND attempts < max_attempts
                )
                OR (
                    state = 'Leased'
                    AND lease_expires_at_utc <= {0}
                )
              )
        ORDER BY priority DESC, available_at_utc, created_at_utc, id
        FOR UPDATE SKIP LOCKED
        LIMIT {1}
        """;
}
