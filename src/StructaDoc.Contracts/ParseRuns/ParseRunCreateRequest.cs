using System.Text.Json;

namespace StructaDoc.Contracts.ParseRuns;

public sealed record ParseRunCreateRequest(
    Guid? ProviderConfigId = null,
    JsonElement? Options = null,
    int? MaxAttempts = null);
