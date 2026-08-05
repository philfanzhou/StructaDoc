using System.Reflection;

namespace StructaDoc.Application;

public static class AssemblyReference
{
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
