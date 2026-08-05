namespace StructaDoc.DatabaseContractTests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DatabaseContractFactAttribute : FactAttribute
{
    public DatabaseContractFactAttribute()
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
