namespace StructaDoc.Contracts.ParseRuns;

/// <summary>
/// Whether this service currently takes queued Parse Runs and sends them to a Provider.
///
/// A queued run that nothing will ever claim looks exactly like one that is about to start, so
/// without this the only honest thing the workspace can say about either is "queued".
///
/// The two switches are reported separately because different people can act on them.
/// <paramref name="WorkerEnabled"/> is a deployment choice and belongs to whoever starts the
/// container; <paramref name="ExecutionEnabled"/> is the one an administrator flips in the browser.
/// </summary>
public sealed record ParseExecutionStatusResponse(bool WorkerEnabled, bool ExecutionEnabled);
