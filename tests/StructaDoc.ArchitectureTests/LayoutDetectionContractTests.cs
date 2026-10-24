using System.Reflection;
using StructaDoc.Adapters.Providers;
using StructaDoc.Application.Providers;

namespace StructaDoc.ArchitectureTests;

/// <summary>
/// Structural guarantees of the layout-detection execution path (ADR-0013): the contract lives in
/// the Application layer, touches no persistence or storage surface, and stays an execution
/// contract separate from the Parse Run Provider registry rather than becoming a document-parsing
/// Provider by another name.
/// </summary>
public sealed class LayoutDetectionContractTests
{
    private static readonly Type[] ApplicationContractTypes =
    [
        typeof(ILayoutDetectionProvider),
        typeof(ILayoutDetectionProviderResolver),
        typeof(LayoutDetectionProviderResolver),
        typeof(LayoutDetectionRequest),
        typeof(LayoutDetectionRegion),
        typeof(LayoutDetectionResult),
        typeof(LayoutRuntimeConfiguration),
    ];

    private static readonly Type[] AdapterTypes =
    [
        typeof(MinerULayoutDetectionProvider),
        typeof(LayoutRuntimeOptions),
    ];

    [Fact]
    public void The_contract_lives_in_the_application_layer()
    {
        // The execution contract belongs beside IParseProvider, where the endpoint task can call
        // it without depending on an adapter. The Domain and Contracts layers hold public data
        // shapes; a layout type there would publish this internal contract as a public one before
        // the endpoint exists.
        foreach (var type in ApplicationContractTypes)
        {
            Assert.Equal("StructaDoc.Application", type.Assembly.GetName().Name);
        }

        foreach (var product in new[]
        {
            StructaDoc.Domain.AssemblyReference.Assembly,
            typeof(StructaDoc.Contracts.System.ServiceInfoResponse).Assembly,
        })
        {
            Assert.All(
                product.GetTypes(),
                type => Assert.False(
                    type.Name.StartsWith("LayoutDetection", StringComparison.Ordinal)
                        || type.Name.StartsWith("LayoutRuntime", StringComparison.Ordinal),
                    $"{product.GetName().Name} defines {type.Name}."));
        }
    }

    [Fact]
    public void Execution_references_no_persistence_storage_or_settings_store()
    {
        // The layout mode creates no Document, Parse Run, Page, Block, Asset, Artifact, or
        // request-result record. At this slice's level that is verified structurally — every type
        // on the execution path, parameters and properties included, must stay clear of the
        // persistence, storage, result-intake, and settings stores — with the end-to-end proof
        // landing with the endpoint task.
        string[] forbiddenNamespacePrefixes =
        [
            "StructaDoc.Adapters.Persistence",
            "StructaDoc.Adapters.Storage",
            "StructaDoc.Adapters.ProviderResults",
            "StructaDoc.Adapters.ControlPlane",
            "StructaDoc.Adapters.Settings",
            "Microsoft.EntityFrameworkCore",
        ];

        foreach (var type in ApplicationContractTypes.Concat(AdapterTypes))
        {
            foreach (var referenced in ReferencedTypes(type))
            {
                Assert.All(
                    forbiddenNamespacePrefixes,
                    prefix => Assert.False(
                        (referenced.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal),
                        $"{type.Name} references {referenced.FullName}."));
            }
        }
    }

    [Fact]
    public void Full_parsing_providers_never_implement_the_layout_contract()
    {
        // The separation runs both ways: no full-parsing provider can be handed a layout request,
        // and the layout adapter can never be resolved as a document-parsing Provider. This is the
        // structural half of the no-fallback guarantee.
        Assert.False(typeof(ILayoutDetectionProvider).IsAssignableFrom(typeof(MinerULocalParseProvider)));
        Assert.False(typeof(ILayoutDetectionProvider).IsAssignableFrom(typeof(MinerUCloudParseProvider)));
        Assert.False(typeof(IParseProvider).IsAssignableFrom(typeof(MinerULayoutDetectionProvider)));
    }

    [Fact]
    public void No_layout_entry_exists_in_the_parse_run_provider_registry()
    {
        // That registry feeds document Parse Run creation; a layout-only runtime must never be
        // offered as a document-parsing Provider (ADR-0013 decision 1 of the settled design).
        var registryValues = typeof(ProviderTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["mineru-cloud", "mineru-local"], registryValues);
        Assert.False(ProviderTypes.IsKnown("mineru-layout"));
    }

    /// <summary>
    /// Every type a type's public surface names: constructor and method parameters and returns,
    /// and property types, with generic arguments — <c>Task&lt;T&gt;</c>, collections, and nullable
    /// references included — expanded recursively.
    /// </summary>
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var members = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)))
            .Concat(type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(property => property.PropertyType));

        foreach (var member in members)
        {
            foreach (var expanded in Expand(member))
            {
                yield return expanded;
            }
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.HasElementType)
        {
            // Arrays and pointers keep their element type; by-ref prefixes resolve to nothing.
            if (type.GetElementType() is { } element)
            {
                foreach (var expanded in Expand(element))
                {
                    yield return expanded;
                }
            }

            yield break;
        }

        yield return type;

        if (type.IsConstructedGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var expanded in Expand(argument))
                {
                    yield return expanded;
                }
            }
        }
    }
}
