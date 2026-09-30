using System.Text;

namespace StructaDoc.Host.Tests;

// Test classes that inspect the ServiceMantle console pipeline swap Console.Out while a host built
// inside the test owns it: the sink binds the writer at host start, so only a host created while
// the capture is installed writes through it. Other test classes start web hosts concurrently, and
// any sink created while this test owns the console keeps whatever writer it bound to. Forwarding
// everything to the original stream and never disposing the capture keeps those hosts working, and
// locking keeps the snapshot coherent, so observing a pipeline's output cannot break another
// test's.
internal sealed class ChainingConsoleWriter(TextWriter original) : TextWriter
{
    private readonly object gate = new();
    private readonly StringBuilder captured = new();

    public override Encoding Encoding => original.Encoding;

    public string Snapshot()
    {
        lock (gate)
        {
            return captured.ToString();
        }
    }

    public override void Write(char value)
    {
        lock (gate)
        {
            captured.Append(value);
            original.Write(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is not null)
        {
            lock (gate)
            {
                captured.Append(value);
                original.Write(value);
            }
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (gate)
        {
            captured.Append(buffer, index, count);
            original.Write(buffer, index, count);
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (gate)
        {
            captured.Append(buffer);
            original.Write(buffer);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (gate)
        {
            if (value is not null)
            {
                captured.Append(value);
            }

            captured.AppendLine();
            original.WriteLine(value);
        }
    }
}
