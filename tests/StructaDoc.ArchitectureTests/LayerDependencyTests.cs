using System.Reflection;

namespace StructaDoc.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_does_not_reference_other_product_layers()
    {
        AssertReferencesOnly(StructaDoc.Domain.AssemblyReference.Assembly);
    }

    [Fact]
    public void Contracts_do_not_reference_other_product_layers()
    {
        AssertReferencesOnly(typeof(StructaDoc.Contracts.System.ServiceInfoResponse).Assembly);
    }

    [Fact]
    public void Application_only_references_domain()
    {
        AssertReferencesOnly(
            Application.AssemblyReference.Assembly,
            "StructaDoc.Domain");
    }

    [Fact]
    public void Adapters_only_reference_inner_layers()
    {
        AssertReferencesOnly(
            Adapters.AssemblyReference.Assembly,
            "StructaDoc.Application",
            "StructaDoc.Domain");
    }

    [Fact]
    public void Migration_assemblies_only_reference_the_adapters_layer()
    {
        var migrationAssemblies = new[]
        {
            typeof(Migrations.Sqlite.SqliteDesignTimeDbContextFactory).Assembly,
            typeof(Migrations.PostgreSql.PostgreSqlDesignTimeDbContextFactory).Assembly,
            typeof(Migrations.MySql.MySqlDesignTimeDbContextFactory).Assembly,
            typeof(Migrations.MariaDb.MariaDbDesignTimeDbContextFactory).Assembly,
        };

        foreach (var migrationAssembly in migrationAssemblies)
        {
            AssertReferencesOnly(migrationAssembly, "StructaDoc.Adapters");
        }
    }

    private static void AssertReferencesOnly(Assembly assembly, params string[] allowedReferences)
    {
        var actualReferences = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("StructaDoc.", StringComparison.Ordinal))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        var unexpectedReferences = actualReferences
            .Except(allowedReferences, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpectedReferences);
    }
}
