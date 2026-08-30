using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StructaDoc.Testing.Persistence;

namespace StructaDoc.Persistence.Tests;

public sealed class DbRowCounterInterceptorTests
{
    [Fact]
    public void Scope_counts_rows_consumed_by_sync_read()
    {
        var counter = new DbCommandCounterInterceptor();
        var options = CreateOptions(counter);
        using var dbContext = new DbContext(options);

        using var scope = counter.BeginScope();
        var values = dbContext.Database
            .SqlQueryRaw<int>(ThreeRowSql)
            .ToList();

        Assert.Equal([1, 2, 3], values);
        Assert.Equal(1, scope.CommandCount);
        Assert.Equal(3, scope.RowCount);
    }

    [Fact]
    public async Task Scope_counts_rows_consumed_by_async_read()
    {
        var counter = new DbCommandCounterInterceptor();
        var options = CreateOptions(counter);
        await using var dbContext = new DbContext(options);

        using var scope = counter.BeginScope();
        var values = await dbContext.Database
            .SqlQueryRaw<int>(ThreeRowSql)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], values);
        Assert.Equal(1, scope.CommandCount);
        Assert.Equal(3, scope.RowCount);
    }

    [Fact]
    public void Scope_stops_counting_an_open_reader_after_the_scope_ends()
    {
        var counter = new DbCommandCounterInterceptor();
        var options = CreateOptions(counter);
        using var dbContext = new DbContext(options);
        using var enumerator = dbContext.Database
            .SqlQueryRaw<int>(ThreeRowSql)
            .AsEnumerable()
            .GetEnumerator();

        var scope = counter.BeginScope();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, scope.RowCount);

        scope.Dispose();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, scope.CommandCount);
        Assert.Equal(1, scope.RowCount);
    }

    [Fact]
    public void Disposing_a_reader_stops_its_count_at_the_rows_already_consumed()
    {
        var counter = new DbCommandCounterInterceptor();
        var options = CreateOptions(counter);
        using var dbContext = new DbContext(options);

        using var scope = counter.BeginScope();
        var enumerator = dbContext.Database
            .SqlQueryRaw<int>(ThreeRowSql)
            .AsEnumerable()
            .GetEnumerator();

        Assert.True(enumerator.MoveNext());
        enumerator.Dispose();

        Assert.Equal(1, scope.CommandCount);
        Assert.Equal(1, scope.RowCount);
    }

    [Fact]
    public async Task Nested_scopes_keep_rows_with_the_scope_that_observed_each_reader()
    {
        var counter = new DbCommandCounterInterceptor();
        var options = CreateOptions(counter);
        await using var outerDbContext = new DbContext(options);
        await using var innerDbContext = new DbContext(options);
        using var outerScope = counter.BeginScope();
        using var outerEnumerator = outerDbContext.Database
            .SqlQueryRaw<int>(ThreeRowSql)
            .AsEnumerable()
            .GetEnumerator();

        Assert.True(outerEnumerator.MoveNext());

        using (var innerScope = counter.BeginScope())
        {
            var innerValues = await innerDbContext.Database
                .SqlQueryRaw<int>(TwoRowSql)
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.Equal([1, 2], innerValues);
            Assert.Equal(1, innerScope.CommandCount);
            Assert.Equal(2, innerScope.RowCount);
            Assert.Equal(1, outerScope.CommandCount);
            Assert.Equal(1, outerScope.RowCount);
        }

        Assert.True(outerEnumerator.MoveNext());
        Assert.Equal(1, outerScope.CommandCount);
        Assert.Equal(2, outerScope.RowCount);
    }

    [Fact]
    public async Task Parallel_scopes_do_not_count_each_others_rows()
    {
        var counter = new DbCommandCounterInterceptor();
        var options = CreateOptions(counter);
        var firstScopeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScopeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScopes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstCount = CountRowsAsync(
            counter,
            options,
            TwoRowSql,
            firstScopeStarted,
            releaseScopes.Task);
        var secondCount = CountRowsAsync(
            counter,
            options,
            ThreeRowSql,
            secondScopeStarted,
            releaseScopes.Task);

        await Task.WhenAll(firstScopeStarted.Task, secondScopeStarted.Task);
        releaseScopes.SetResult();

        Assert.Equal(2, await firstCount);
        Assert.Equal(3, await secondCount);
    }

    [Fact]
    public async Task Row_count_distinguishes_linear_and_multiplicative_single_queries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var counter = new DbCommandCounterInterceptor();
        var options = new DbContextOptionsBuilder<QueryShapeDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;
        await using var dbContext = new QueryShapeDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        dbContext.Parents.Add(new QueryParent
        {
            FirstChildren = [new(), new()],
            SecondChildren = [new(), new(), new()],
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbContext.ChangeTracker.Clear();

        using var linearScope = counter.BeginScope();
        _ = await dbContext.Parents
            .AsNoTracking()
            .Include(parent => parent.FirstChildren)
            .SingleAsync(TestContext.Current.CancellationToken);
        linearScope.Dispose();

        using var multiplicativeScope = counter.BeginScope();
        _ = await dbContext.Parents
            .AsNoTracking()
            .Include(parent => parent.FirstChildren)
            .Include(parent => parent.SecondChildren)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, linearScope.CommandCount);
        Assert.Equal(2, linearScope.RowCount);
        Assert.Equal(1, multiplicativeScope.CommandCount);
        Assert.Equal(6, multiplicativeScope.RowCount);
    }

    private const string TwoRowSql =
        "SELECT 1 AS Value UNION ALL SELECT 2 AS Value ORDER BY Value";

    private const string ThreeRowSql =
        "SELECT 1 AS Value UNION ALL SELECT 2 AS Value UNION ALL " +
        "SELECT 3 AS Value ORDER BY Value";

    private static DbContextOptions CreateOptions(DbCommandCounterInterceptor counter) =>
        new DbContextOptionsBuilder()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(counter)
            .Options;

    private static async Task<long> CountRowsAsync(
        DbCommandCounterInterceptor counter,
        DbContextOptions options,
        string sql,
        TaskCompletionSource scopeStarted,
        Task releaseScope)
    {
        await using var dbContext = new DbContext(options);
        using var scope = counter.BeginScope();
        scopeStarted.SetResult();
        await releaseScope;

        _ = await dbContext.Database
            .SqlQueryRaw<int>(sql)
            .ToListAsync(TestContext.Current.CancellationToken);
        return scope.RowCount;
    }

    private sealed class QueryShapeDbContext(
        DbContextOptions<QueryShapeDbContext> options) : DbContext(options)
    {
        public DbSet<QueryParent> Parents => Set<QueryParent>();
    }

    private sealed class QueryParent
    {
        public int Id { get; set; }

        public List<FirstChild> FirstChildren { get; set; } = [];

        public List<SecondChild> SecondChildren { get; set; } = [];
    }

    private sealed class FirstChild
    {
        public int Id { get; set; }
    }

    private sealed class SecondChild
    {
        public int Id { get; set; }
    }
}
