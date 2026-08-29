using System.Runtime.CompilerServices;

namespace StructaDoc.Persistence.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LibreOfficeIntegrationTheoryAttribute : TheoryAttribute
{
    public LibreOfficeIntegrationTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "STRUCTADOC_RUN_LIBREOFFICE_INTEGRATION_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set STRUCTADOC_RUN_LIBREOFFICE_INTEGRATION_TESTS=1 to run LibreOffice integration tests.";
        }
    }
}
