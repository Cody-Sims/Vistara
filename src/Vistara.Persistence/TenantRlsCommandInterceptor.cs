using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Vistara.Persistence;

public sealed class TenantRlsCommandInterceptor(ITenantScope tenantScope)
    : DbCommandInterceptor
{
    public const string TenantSettingName = "vistara.tenant_id";
    public const string SetTenantSql =
        "SELECT set_config('vistara.tenant_id', @tenant_id, true);";

    private readonly ITenantScope _tenantScope =
        tenantScope ?? throw new ArgumentNullException(nameof(tenantScope));
    private readonly ConditionalWeakTable<DbCommand, OwnedTransaction> _owned = new();
    private readonly ConditionalWeakTable<DbTransaction, TransactionTenant> _tenants = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Prepare(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>>
        ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
    {
        await PrepareAsync(command, cancellationToken);
        return result;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result) =>
        WrapOwnedReader(command, result);

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(WrapOwnedReader(command, result));

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Prepare(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(command, cancellationToken);
        return result;
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        CompleteOwned(command);
        return result;
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        await CompleteOwnedAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Prepare(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(command, cancellationToken);
        return result;
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        CompleteOwned(command);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await CompleteOwnedAsync(command, cancellationToken);
        return result;
    }

    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData) =>
        AbortOwned(command);

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default) =>
        AbortOwnedAsync(command, cancellationToken).AsTask();

    public override void CommandCanceled(
        DbCommand command,
        CommandEndEventData eventData) =>
        AbortOwned(command);

    public override Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        AbortOwnedAsync(command, cancellationToken).AsTask();

    private void Prepare(DbCommand command)
    {
        Guid tenantId = TenantScopeGuard.RequireTenantId(_tenantScope);
        if (command.Connection is not NpgsqlConnection connection)
        {
            return;
        }

        DbTransaction transaction = command.Transaction ??
            connection.BeginTransaction();
        bool ownsTransaction = command.Transaction is null;
        command.Transaction = transaction;
        try
        {
            EstablishTransactionTenant(transaction, tenantId);
            SetTenant(command.Connection, transaction, tenantId);
            if (ownsTransaction)
            {
                _owned.Add(command, new OwnedTransaction(transaction));
            }
        }
        catch
        {
            if (ownsTransaction)
            {
                transaction.Rollback();
                transaction.Dispose();
            }

            throw;
        }
    }

    private async ValueTask PrepareAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        Guid tenantId = TenantScopeGuard.RequireTenantId(_tenantScope);
        if (command.Connection is not NpgsqlConnection connection)
        {
            return;
        }

        DbTransaction transaction = command.Transaction ??
            await connection.BeginTransactionAsync(cancellationToken);
        bool ownsTransaction = command.Transaction is null;
        command.Transaction = transaction;
        try
        {
            EstablishTransactionTenant(transaction, tenantId);
            await SetTenantAsync(
                command.Connection,
                transaction,
                tenantId,
                cancellationToken);
            if (ownsTransaction)
            {
                _owned.Add(command, new OwnedTransaction(transaction));
            }
        }
        catch
        {
            if (ownsTransaction)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
            }

            throw;
        }
    }

    private void EstablishTransactionTenant(
        DbTransaction transaction,
        Guid tenantId)
    {
        TransactionTenant established = _tenants.GetValue(
            transaction,
            _ => new TransactionTenant(tenantId));
        if (established.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                "A database transaction cannot change tenant scope.");
        }
    }

    private static void SetTenant(
        DbConnection connection,
        DbTransaction transaction,
        Guid tenantId)
    {
        using DbCommand command = CreateSetTenantCommand(
            connection,
            transaction,
            tenantId);
        _ = command.ExecuteScalar();
    }

    private static async ValueTask SetTenantAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateSetTenantCommand(
            connection,
            transaction,
            tenantId);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private static DbCommand CreateSetTenantCommand(
        DbConnection connection,
        DbTransaction transaction,
        Guid tenantId)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SetTenantSql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.DbType = DbType.String;
        parameter.Value = tenantId.ToString("D", CultureInfo.InvariantCulture);
        command.Parameters.Add(parameter);
        return command;
    }

    private DbDataReader WrapOwnedReader(
        DbCommand command,
        DbDataReader reader)
    {
        if (!TryTakeOwned(command, out OwnedTransaction owned))
        {
            return reader;
        }

        return new TenantTransactionDataReader(reader, owned.Transaction);
    }

    private void CompleteOwned(DbCommand command)
    {
        if (TryTakeOwned(command, out OwnedTransaction owned))
        {
            try
            {
                owned.Transaction.Commit();
            }
            finally
            {
                owned.Transaction.Dispose();
            }
        }
    }

    private async ValueTask CompleteOwnedAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (TryTakeOwned(command, out OwnedTransaction owned))
        {
            try
            {
                await owned.Transaction.CommitAsync(CancellationToken.None);
            }
            finally
            {
                await owned.Transaction.DisposeAsync();
            }
        }
    }

    private void AbortOwned(DbCommand command)
    {
        if (TryTakeOwned(command, out OwnedTransaction owned))
        {
            try
            {
                owned.Transaction.Rollback();
            }
            finally
            {
                owned.Transaction.Dispose();
            }
        }
    }

    private async ValueTask AbortOwnedAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (TryTakeOwned(command, out OwnedTransaction owned))
        {
            try
            {
                await owned.Transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                await owned.Transaction.DisposeAsync();
            }
        }
    }

    private bool TryTakeOwned(
        DbCommand command,
        out OwnedTransaction owned)
    {
        if (_owned.TryGetValue(command, out OwnedTransaction? value) &&
            value is not null)
        {
            owned = value;
            _owned.Remove(command);
            return true;
        }

        owned = null!;
        return false;
    }

    private sealed record OwnedTransaction(DbTransaction Transaction);

    private sealed record TransactionTenant(Guid TenantId);

    private sealed class TenantTransactionDataReader(
        DbDataReader inner,
        DbTransaction transaction) : DbDataReader
    {
        private bool _failed;
        private bool _disposed;

        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override int VisibleFieldCount => inner.VisibleFieldCount;

        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length) =>
            inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length) =>
            inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) =>
            inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) =>
            inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) =>
            inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) =>
            inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) =>
            inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override T GetFieldValue<T>(int ordinal) =>
            inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(
            int ordinal,
            CancellationToken cancellationToken) =>
            inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override IEnumerator GetEnumerator() =>
            ((IEnumerable)inner).GetEnumerator();

        public override bool NextResult()
        {
            try
            {
                return inner.NextResult();
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        public override async Task<bool> NextResultAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await inner.NextResultAsync(cancellationToken);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        public override bool Read()
        {
            try
            {
                return inner.Read();
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        public override async Task<bool> ReadAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ReadAsync(cancellationToken);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        public override void Close()
        {
            if (_disposed)
            {
                return;
            }

            inner.Close();
            Finish();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                inner.Dispose();
                Finish();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await inner.DisposeAsync();
                await FinishAsync();
            }

            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void Finish()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_failed)
            {
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
            }

            transaction.Dispose();
        }

        private async ValueTask FinishAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_failed)
            {
                await transaction.RollbackAsync();
            }
            else
            {
                await transaction.CommitAsync();
            }

            await transaction.DisposeAsync();
        }
    }
}
