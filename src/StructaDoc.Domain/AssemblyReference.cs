using System.Reflection;

namespace StructaDoc.Domain;

public static class AssemblyReference
{
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
