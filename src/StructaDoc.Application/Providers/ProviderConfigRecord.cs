namespace StructaDoc.Application.Providers;

public sealed record ProviderConfigRecord(
    Guid Id,
    string Name,
    string ProviderType,
    bool IsEnabled,
    bool IsDefault,
    Guid CurrentVersionId,
    int VersionNumber,
    string BaseUrl,
    string? Model,
    string? Backend,
    bool HasCredential,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public enum ProviderConfigMutationStatus
{
    Succeeded,
    NotFound,
    Conflict,
}

public sealed record ProviderConfigMutationResult(
    ProviderConfigMutationStatus Status,
    ProviderConfigRecord? Config = null);

/// <summary>
/// Why a Provider configuration could or could not be removed. The two refusals are separate
/// because they ask the administrator for different things: one is waited out, the other never
/// clears on its own and means the configuration should be disabled instead.
/// </summary>
public enum ProviderConfigDeletionStatus
{
    Deleted,
    NotFound,

    /// <summary>
    /// A Parse Run that has not reached a final status still reads this configuration while it
    /// executes, so removing it would break a run already under way.
    /// </summary>
    ReferencedByActiveParseRun,

    /// <summary>
    /// Only finished Parse Runs reference it, and each of them records the configuration version it
    /// was produced with. Removing it would erase that record rather than free anything.
    /// </summary>
    ReferencedByParseHistory,
}
