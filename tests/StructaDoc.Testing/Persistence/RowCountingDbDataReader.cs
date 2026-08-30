using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;

namespace StructaDoc.Testing.Persistence;

internal sealed class RowCountingDbDataReader(
    DbDataReader reader,
    DbCommandCounterInterceptor.ScopeState scopeState) :
    DbDataReader,
    IDbColumnSchemaGenerator
{
    private int stopped;

    public override int Depth => reader.Depth;

    public override int FieldCount => reader.FieldCount;

    public override bool HasRows => reader.HasRows;

    public override bool IsClosed => reader.IsClosed;

    public override int RecordsAffected => reader.RecordsAffected;

    public override int VisibleFieldCount => reader.VisibleFieldCount;

    public override object this[int ordinal] => reader[ordinal];

    public override object this[string name] => reader[name];

    public override void Close()
    {
        StopCounting();
        reader.Close();
    }

    public override async Task CloseAsync()
    {
        StopCounting();
        await reader.CloseAsync().ConfigureAwait(false);
    }

    public override bool GetBoolean(int ordinal) => reader.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => reader.GetByte(ordinal);

    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length) => reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => reader.GetChar(ordinal);

    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length) => reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public ReadOnlyCollection<DbColumn> GetColumnSchema() => reader.GetColumnSchema();

    public override Task<ReadOnlyCollection<DbColumn>> GetColumnSchemaAsync(
        CancellationToken cancellationToken = default) =>
        reader.GetColumnSchemaAsync(cancellationToken);

    public override string GetDataTypeName(int ordinal) => reader.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => reader.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => reader.GetDouble(ordinal);

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);

    public override T GetFieldValue<T>(int ordinal) => reader.GetFieldValue<T>(ordinal);

    public override Task<T> GetFieldValueAsync<T>(
        int ordinal,
        CancellationToken cancellationToken) =>
        reader.GetFieldValueAsync<T>(ordinal, cancellationToken);

    public override float GetFloat(int ordinal) => reader.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => reader.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => reader.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => reader.GetInt64(ordinal);

    public override string GetName(int ordinal) => reader.GetName(ordinal);

    public override int GetOrdinal(string name) => reader.GetOrdinal(name);

    public override Type GetProviderSpecificFieldType(int ordinal) =>
        reader.GetProviderSpecificFieldType(ordinal);

    public override object GetProviderSpecificValue(int ordinal) =>
        reader.GetProviderSpecificValue(ordinal);

    public override int GetProviderSpecificValues(object[] values) =>
        reader.GetProviderSpecificValues(values);

    public override DataTable? GetSchemaTable() => reader.GetSchemaTable();

    public override Task<DataTable?> GetSchemaTableAsync(
        CancellationToken cancellationToken = default) =>
        reader.GetSchemaTableAsync(cancellationToken);

    public override Stream GetStream(int ordinal) => reader.GetStream(ordinal);

    public override string GetString(int ordinal) => reader.GetString(ordinal);

    public override TextReader GetTextReader(int ordinal) => reader.GetTextReader(ordinal);

    public override object GetValue(int ordinal) => reader.GetValue(ordinal);

    public override int GetValues(object[] values) => reader.GetValues(values);

    public override bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);

    public override Task<bool> IsDBNullAsync(
        int ordinal,
        CancellationToken cancellationToken) =>
        reader.IsDBNullAsync(ordinal, cancellationToken);

    public override bool NextResult() => reader.NextResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
        reader.NextResultAsync(cancellationToken);

    public override bool Read()
    {
        var hasRow = reader.Read();
        CountRow(hasRow);
        return hasRow;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        CountRow(hasRow);
        return hasRow;
    }

    public override async ValueTask DisposeAsync()
    {
        StopCounting();
        await reader.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        StopCounting();
        if (disposing)
        {
            reader.Dispose();
        }
    }

    protected override DbDataReader GetDbDataReader(int ordinal) =>
        (DbDataReader)reader.GetData(ordinal);

    private void CountRow(bool hasRow)
    {
        if (hasRow && Volatile.Read(ref stopped) == 0 && !scopeState.IsDisposed)
        {
            Interlocked.Increment(ref scopeState.RowCount);
        }
    }

    private void StopCounting() => Volatile.Write(ref stopped, 1);
}
