using Microsoft.EntityFrameworkCore;
using StructaDoc.Testing.Persistence;

namespace StructaDoc.Persistence.Tests;

public sealed class DbCommandCounterInterceptorTests
{
    [Fact]
    public async Task Scope_counts_sync_and_async_commands_once_and_ignores_commands_outside_it()
    {
        var commandCounter = new DbCommandCounterInterceptor();
        var options = CreateOptions(commandCounter);
        await using var dbContext = new DbContext(options);

        dbContext.Database.ExecuteSqlRaw("SELECT 1");

        var commandScope = commandCounter.BeginScope();
        dbContext.Database.ExecuteSqlRaw("SELECT 1");
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT 1",
            TestContext.Current.CancellationToken);
        commandScope.Dispose();

        dbContext.Database.ExecuteSqlRaw("SELECT 1");

        Assert.Equal(2, commandScope.CommandCount);
    }

    [Fact]
    public void Nested_scopes_count_only_the_innermost_active_scope()
    {
        var commandCounter = new DbCommandCounterInterceptor();
        var options = CreateOptions(commandCounter);
        using var dbContext = new DbContext(options);

        using var outerScope = commandCounter.BeginScope();
        dbContext.Database.ExecuteSqlRaw("SELECT 1");

        using (var innerScope = commandCounter.BeginScope())
        {
            dbContext.Database.ExecuteSqlRaw("SELECT 1");
            dbContext.Database.ExecuteSqlRaw("SELECT 1");

            Assert.Equal(2, innerScope.CommandCount);
            Assert.Equal(1, outerScope.CommandCount);
        }

        dbContext.Database.ExecuteSqlRaw("SELECT 1");

        Assert.Equal(2, outerScope.CommandCount);
    }

    [Fact]
    public async Task Parallel_scopes_do_not_count_each_others_commands()
    {
        var commandCounter = new DbCommandCounterInterceptor();
        var options = CreateOptions(commandCounter);
        var firstScopeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScopeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScopes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstCount = CountCommandsAsync(
            commandCounter,
            options,
            commandCount: 1,
            firstScopeStarted,
            releaseScopes.Task);
        var secondCount = CountCommandsAsync(
            commandCounter,
            options,
            commandCount: 3,
            secondScopeStarted,
            releaseScopes.Task);

        await Task.WhenAll(firstScopeStarted.Task, secondScopeStarted.Task);
        releaseScopes.SetResult();

        Assert.Equal(1, await firstCount);
        Assert.Equal(3, await secondCount);
    }

    private static DbContextOptions CreateOptions(
        DbCommandCounterInterceptor commandCounter) =>
        new DbContextOptionsBuilder()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(commandCounter)
            .Options;

    private static async Task<int> CountCommandsAsync(
        DbCommandCounterInterceptor commandCounter,
        DbContextOptions options,
        int commandCount,
        TaskCompletionSource scopeStarted,
        Task releaseScope)
    {
        await using var dbContext = new DbContext(options);
        using var commandScope = commandCounter.BeginScope();
        scopeStarted.SetResult();
        await releaseScope;

        for (var index = 0; index < commandCount; index++)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT 1",
                TestContext.Current.CancellationToken);
        }

        return commandScope.CommandCount;
    }
}
