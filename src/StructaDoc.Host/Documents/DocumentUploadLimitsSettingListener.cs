using System.Globalization;
using StructaDoc.Application.Documents;
using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Documents;

/// <summary>
/// Applies <see cref="SettingCatalog.MaxUploadBytes"/> to the running upload limit, which both the
/// endpoint's request-size bound and the storage write during ingestion read per request. A change
/// the limit cannot use returns <see langword="false"/>, so the write is answered with the restart
/// it still needs rather than a silent no-op.
/// </summary>
public sealed class DocumentUploadLimitsSettingListener(
    DocumentUploadLimits limits,
    ILogger<DocumentUploadLimitsSettingListener> logger) : ISettingChangeListener
{
    public Task<bool> TryApplyAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(key, SettingCatalog.MaxUploadBytes, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxUploadBytes)
            || !limits.TrySet(maxUploadBytes))
        {
            return Task.FromResult(false);
        }

        logger.LogInformation(
            "The upload size limit changed to {MaxUploadBytes} bytes and applies to requests from this point on.",
            maxUploadBytes);
        return Task.FromResult(true);
    }

    public bool TryGetAppliedValue(string key, out string? appliedValue)
    {
        if (!string.Equals(key, SettingCatalog.MaxUploadBytes, StringComparison.Ordinal))
        {
            appliedValue = null;
            return false;
        }

        appliedValue = limits.MaxUploadBytes.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
