namespace StructaDoc.Testing;

/// <summary>
/// Removes a directory a test created, together with everything in it.
/// </summary>
/// <remarks>
/// A sharing violation is waited out for a short, bounded time instead of failing the test. Tests
/// delete their directories right after closing the database inside, and on Windows a real-time
/// scanner can still hold that file for a few milliseconds at exactly that moment. That lock is
/// transient and outside the test process, so it says nothing about the test; one that outlasts
/// the wait is reported the way a plain delete reports it.
/// </remarks>
public static class TestDirectory
{
    private const int RetryDelayMilliseconds = 50;

    // About five seconds; the lock this exists for lasted four milliseconds when it was measured.
    private const int RetryLimit = 100;

    public static void Delete(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (
                attempt < RetryLimit
                && error is IOException or UnauthorizedAccessException
                && Directory.Exists(path))
            {
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }
}
