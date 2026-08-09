using System.Reflection;

namespace StructaDoc.Adapters;

public static class AssemblyReference
{
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
