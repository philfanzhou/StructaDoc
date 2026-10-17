namespace StructaDoc.Host.Tests;

// The ServiceMantle console sink resolves Console.Out when each log event is emitted, so while a
// console-capture test owns the standard output, any other Console.SetOut steals its events — the
// assertion then reads a snapshot that missed the lines it is waiting for. The test classes that
// swap Console.Out share this collection and therefore run one at a time, which is the only
// process-wide guarantee the capture tests need; classes that never touch the console keep running
// in parallel.
[CollectionDefinition("Console output", DisableParallelization = true)]
public sealed class ConsoleOutputCollection;
