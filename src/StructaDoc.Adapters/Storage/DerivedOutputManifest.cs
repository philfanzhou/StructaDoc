using System.Net;
using System.Text.Json;
using Amazon.S3;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.Storage;

namespace StructaDoc.Adapters.Storage;

/// <summary>
/// The internal deletion inventory for one normalization identity (a Parse Run, or a large-PDF
/// Segment normalized under its own identity). The normalizer derives every deterministic output
/// key from the validated Provider archive and persists this manifest before its first derived
/// write, so deletion can discover outputs that never reached committed canonical rows — for
/// example after a validation failure, a partial write, or cancellation. The manifest is cleanup
/// metadata owned by the service: it is not a public Artifact, not Provider payload, and never
/// appears in API responses.
/// </summary>
public static class DerivedOutputManifest
{
    public const int CurrentVersion = 1;
    public const string FileName = "derived-manifest.json";

    // One manifest carries at most the normalizer's fixed artifact names plus the bounded asset
    // set, so a stored document can never grow the inventory past the normalization limits.
    public const int MaxArtifactKeys = 8;
    public const int MaxOutputs = ParseBundleValidator.MaxAssets + MaxArtifactKeys;
    public const int MaxJsonBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string StorageKey(Guid identity) => $"parse-runs/{identity:N}/provider/{FileName}";

    /// <summary>
    /// Serializes the manifest deterministically: the same identity, source archive, and planned
    /// keys always produce identical bytes, so an immutable/idempotent storage write either stores
    /// it once or reports a conflict instead of silently replacing a different inventory.
    /// </summary>
    public static string Serialize(
        Guid identity,
        string sourceArchiveSha256,
        IReadOnlyList<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchiveSha256);
        if (sourceArchiveSha256.Length != 64 || !sourceArchiveSha256.All(IsLowerCaseHex))
        {
            throw new ArgumentException(
                "The source archive digest must be 64 lowercase hexadecimal characters.",
                nameof(sourceArchiveSha256));
        }

        if (outputs.Count > MaxOutputs
            || outputs.Any(key => !IsAllowedOutputKey(key, identity))
            || outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Count)
        {
            throw new ArgumentException(
                "The derived-output inventory is invalid for this normalization identity.",
                nameof(outputs));
        }

        return JsonSerializer.Serialize(
            new ManifestData(CurrentVersion, identity.ToString("N"), sourceArchiveSha256, outputs),
            SerializerOptions);
    }

    /// <summary>
    /// Reads the stored manifest for one identity. Returns <see langword="null"/> only when the
    /// manifest object is truly absent, which is the legacy case created before manifests existed;
    /// a storage outage propagates so deletion fails closed instead of silently losing coverage.
    /// Stored manifest JSON is untrusted content: every field is validated and any violation
    /// throws before the caller mutates lifecycle state.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The stored manifest is malformed or does not match <paramref name="identity"/>.
    /// </exception>
    public static async Task<IReadOnlyList<string>?> ReadAsync(
        IFileStorage storage,
        Guid identity,
        string identityDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDescription);

        byte[] json;
        try
        {
            await using var stream = await storage.OpenReadAsync(
                StorageKey(identity),
                cancellationToken);
            json = await ReadBoundedAsync(stream, identityDescription, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return Parse(json, identity, identityDescription);
    }

    /// <summary>
    /// Validates stored manifest bytes against the identity being deleted and returns its planned
    /// output keys. Descriptions never quote manifest content, so untrusted values cannot leak
    /// into exception text.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The manifest is malformed or does not match <paramref name="identity"/>.
    /// </exception>
    public static IReadOnlyList<string> Parse(byte[] json, Guid identity, string identityDescription)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDescription);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Invalid(identityDescription, "is not valid manifest JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(identityDescription, "has an invalid structure.");
            }

            var sawVersion = false;
            var sawIdentity = false;
            var sawDigest = false;
            var sawOutputs = false;
            var version = 0;
            var manifestIdentity = string.Empty;
            var digest = string.Empty;
            JsonElement outputsElement = default;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "version":
                        if (sawVersion
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out version))
                        {
                            throw Invalid(identityDescription, "has an invalid structure.");
                        }

                        sawVersion = true;
                        break;
                    case "identity":
                        if (sawIdentity || property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw Invalid(identityDescription, "has an invalid structure.");
                        }

                        manifestIdentity = property.Value.GetString()!;
                        sawIdentity = true;
                        break;
                    case "sourceArchiveSha256":
                        if (sawDigest || property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw Invalid(identityDescription, "has an invalid structure.");
                        }

                        digest = property.Value.GetString()!;
                        sawDigest = true;
                        break;
                    case "outputs":
                        if (sawOutputs || property.Value.ValueKind != JsonValueKind.Array)
                        {
                            throw Invalid(identityDescription, "has an invalid structure.");
                        }

                        outputsElement = property.Value.Clone();
                        sawOutputs = true;
                        break;
                    default:
                        throw Invalid(identityDescription, "has an unrecognized field.");
                }
            }

            if (!sawVersion || !sawIdentity || !sawDigest || !sawOutputs)
            {
                throw Invalid(identityDescription, "has an invalid structure.");
            }

            if (version != CurrentVersion)
            {
                throw Invalid(identityDescription, "has an unsupported version.");
            }

            if (!Guid.TryParseExact(manifestIdentity, "N", out var parsedIdentity)
                || parsedIdentity != identity)
            {
                throw Invalid(identityDescription, "does not match the resource being deleted.");
            }

            if (digest.Length != 64 || !digest.All(IsLowerCaseHex))
            {
                throw Invalid(identityDescription, "has an invalid source-archive digest.");
            }

            var outputs = new List<string>(outputsElement.GetArrayLength());
            foreach (var item in outputsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw Invalid(identityDescription, "has an invalid structure.");
                }

                var key = item.GetString()!;
                if (!IsAllowedOutputKey(key, identity))
                {
                    throw Invalid(identityDescription, "lists an output outside its allowed prefixes.");
                }

                if (outputs.Contains(key, StringComparer.Ordinal))
                {
                    throw Invalid(identityDescription, "lists duplicate outputs.");
                }

                outputs.Add(key);
            }

            if (outputs.Count > MaxOutputs)
            {
                throw Invalid(identityDescription, "lists too many outputs.");
            }

            return outputs;
        }
    }

    /// <summary>
    /// A planned key is trusted only when it sits inside the artifacts or assets prefix of this
    /// exact identity and its name is a plain safe file name, so stored JSON cannot redirect
    /// deletion at another run's objects or outside the per-run tree.
    /// </summary>
    public static bool IsAllowedOutputKey(string key, Guid identity) =>
        HasAllowedPrefixedName(key, $"parse-runs/{identity:N}/artifacts/")
        || HasAllowedPrefixedName(key, $"parse-runs/{identity:N}/assets/");

    private static bool HasAllowedPrefixedName(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = key[prefix.Length..];
        return name.Length is > 0 and <= 128
            && name[0] != '.'
            && name.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        string identityDescription,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var content = new MemoryStream();
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return content.ToArray();
            }

            totalBytes += bytesRead;
            if (totalBytes > MaxJsonBytes)
            {
                throw Invalid(identityDescription, "exceeds the supported size.");
            }

            await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private static bool IsLowerCaseHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static InvalidDataException Invalid(string identityDescription, string reason) => new(
        $"The derived-output manifest for {identityDescription} {reason} "
        + "Restore or repair this record before requesting deletion again.");

    private sealed record ManifestData(
        int Version,
        string Identity,
        string SourceArchiveSha256,
        IReadOnlyList<string> Outputs);
}
