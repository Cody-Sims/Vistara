using System.Data.Common;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vistara.Persistence;

/// <summary>
/// How a relational write failed, as far as the caller can act on it.
/// </summary>
public enum RelationalFaultKind
{
    /// <summary>The failure is not a recognised database fault.</summary>
    Unknown,

    /// <summary>An optimistic concurrency fence rejected the write.</summary>
    Concurrency,

    /// <summary>
    /// A declared precondition rejected the write, such as a unique, check,
    /// foreign key, or exclusion constraint. Repeating the same write fails
    /// the same way.
    /// </summary>
    Precondition,

    /// <summary>
    /// The database or its connection could not complete the write: a
    /// connection fault, timeout, serialization failure, deadlock, resource
    /// exhaustion, or an otherwise unattributed statement failure. Repeating
    /// the write later can succeed.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Classifies provider failures by the SQLite result code and PostgreSQL
/// SQLSTATE the provider reports, so callers never inspect message text.
/// </summary>
public static class RelationalFaultClassifier
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;

    public static RelationalFaultKind Classify(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        for (Exception? current = failure;
             current is not null;
             current = current.InnerException)
        {
            switch (current)
            {
                case DbUpdateConcurrencyException:
                    return RelationalFaultKind.Concurrency;
                case SqliteException sqlite:
                    return sqlite.SqliteErrorCode == SqliteConstraint
                        ? RelationalFaultKind.Precondition
                        : RelationalFaultKind.Unavailable;
                case PostgresException postgres:
                    return IsIntegrityConstraintViolation(postgres.SqlState)
                        ? RelationalFaultKind.Precondition
                        : RelationalFaultKind.Unavailable;
                case DbException:
                case TimeoutException:
                case IOException:
                case SocketException:
                    return RelationalFaultKind.Unavailable;
                default:
                    continue;
            }
        }

        return RelationalFaultKind.Unknown;
    }

    /// <summary>
    /// Reports whether a failure is database contention or a constraint
    /// violation. Callers that only need to know whether a competing writer
    /// may have won use this coarser answer.
    /// </summary>
    public static bool IsContentionOrConstraint(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        for (Exception? current = failure;
             current is not null;
             current = current.InnerException)
        {
            switch (current)
            {
                case SqliteException sqlite when sqlite.SqliteErrorCode is
                    SqliteBusy or SqliteLocked or SqliteConstraint:
                case SqliteException locked when locked.SqliteExtendedErrorCode is
                    261 or 262:
                    return true;
                case PostgresException postgres when postgres.SqlState is
                    "23505" or "23514" or "40001" or "40P01" or "55P03":
                    return true;
                default:
                    continue;
            }
        }

        return false;
    }

    /// <summary>
    /// SQLSTATE class 23 is the integrity constraint violation class: unique,
    /// check, foreign key, not-null, and exclusion violations.
    /// </summary>
    private static bool IsIntegrityConstraintViolation(string sqlState) =>
        sqlState.StartsWith("23", StringComparison.Ordinal);
}
