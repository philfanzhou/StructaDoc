namespace StructaDoc.Contracts.ParseRuns;

/// <summary>
/// Whether this service takes queued Parse Runs and sends them to a Provider.
///
/// A queued run that nothing will ever claim looks exactly like one that is about to start, so
/// without this the only honest thing the workspace can say about either is "queued".
///
/// <paramref name="WorkerEnabled"/> is a deployment choice, made by whoever starts the container so
/// that a Host can serve the API while other Hosts do the parsing. It is not settable from a browser,
/// which is why the workspace can only report it rather than offer to fix it.
/// </summary>
public sealed record ParseExecutionStatusResponse(bool WorkerEnabled);
