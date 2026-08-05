using System.Reflection;

namespace StructaDoc.Infrastructure;

public static class AssemblyReference
{
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
