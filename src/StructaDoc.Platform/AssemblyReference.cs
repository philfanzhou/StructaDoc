using System.Reflection;

namespace StructaDoc.Platform;

public static class AssemblyReference
{
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
