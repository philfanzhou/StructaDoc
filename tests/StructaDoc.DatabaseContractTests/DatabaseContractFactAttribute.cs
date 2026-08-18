using System.Runtime.CompilerServices;

namespace StructaDoc.DatabaseContractTests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DatabaseContractFactAttribute : FactAttribute
{
    // Both parameters belong to the compiler rather than to any caller. xunit reads the file and line
    // off FactAttribute to say where a test is declared, and a derived attribute that does not forward
    // them hands every test it marks the location of this constructor instead. No call site passes them.
    public DatabaseContractFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1 to run container database tests.";
        }
    }
}
