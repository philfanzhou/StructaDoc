using StructaDoc.Application.Providers;

namespace StructaDoc.Adapters.Providers;

/// <summary>
/// The bound form of the <c>Layout</c> settings section: which backend serves the non-archiving
/// layout-detection mode (ADR-0014). Following <c>LayoutRuntimeOptions</c>, the section binds
/// recoverably at startup, so a stored value that fails <see cref="Validate"/> is dropped and the
/// service starts on the default backend.
///
/// An absent or blank value is not a validation failure: it is the shipped state that selects the
/// optional layout inference runtime, so a deployment that never heard of the cloud route keeps
/// its exact behavior. The cloud choice is deliberate — no fallback ever selects it.
/// </summary>
public sealed class LayoutOptions
{
    public const string SectionName = "Layout";

    public string Backend { get; init; } = LayoutBackendNames.LayoutRuntime;

    public void Validate()
    {
        if (!LayoutBackendNames.IsKnown(Backend))
        {
            throw new InvalidOperationException(
                "Layout:Backend must be 'layout-runtime' or 'mineru-cloud'.");
        }
    }

    public LayoutBackend ToBackend() =>
        Backend == LayoutBackendNames.MinerUCloud ? LayoutBackend.MinerUCloud : LayoutBackend.LayoutRuntime;
}
