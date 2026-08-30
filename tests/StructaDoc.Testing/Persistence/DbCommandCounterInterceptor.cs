using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace StructaDoc.Testing.Persistence;

public sealed class DbCommandCounterInterceptor : DbCommandInterceptor
{
    private readonly AsyncLocal<ScopeState?> currentScope = new();

    public DbCommandCountScope BeginScope()
    {
        var state = new ScopeState(currentScope.Value);
        currentScope.Value = state;
        return new DbCommandCountScope(this, state);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        CountCommand();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CountCommand();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        CountCommand();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        CountCommand();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        CountCommand();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        CountCommand();
        return ValueTask.FromResult(result);
    }

    private void CountCommand()
    {
        var state = currentScope.Value;
        if (state is not null && !state.IsDisposed)
        {
            Interlocked.Increment(ref state.CommandCount);
        }
    }

    private void EndScope(ScopeState state)
    {
        if (state.IsDisposed)
        {
            return;
        }

        if (!ReferenceEquals(currentScope.Value, state))
        {
            throw new InvalidOperationException(
                "Database command count scopes must be disposed in reverse creation order.");
        }

        currentScope.Value = state.Parent;
        Volatile.Write(ref state.Disposed, 1);
    }

    internal sealed class ScopeState(ScopeState? parent)
    {
        public int CommandCount;

        public int Disposed;

        public ScopeState? Parent { get; } = parent;

        public bool IsDisposed => Volatile.Read(ref Disposed) != 0;
    }

    public sealed class DbCommandCountScope : IDisposable
    {
        private readonly DbCommandCounterInterceptor owner;
        private readonly ScopeState state;

        internal DbCommandCountScope(DbCommandCounterInterceptor owner, ScopeState state)
        {
            this.owner = owner;
            this.state = state;
        }

        public int CommandCount => Volatile.Read(ref state.CommandCount);

        public void Dispose() => owner.EndScope(state);
    }
}
