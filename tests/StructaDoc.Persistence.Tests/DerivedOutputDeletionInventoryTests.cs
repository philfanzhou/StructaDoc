using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Resources;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.Authentication;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Resources;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Domain.Resources;
using StructaDoc.Migrations.Sqlite;

namespace StructaDoc.Persistence.Tests;

/// <summary>
/// Deletion-inventory coverage for derived normalization outputs: the internal manifest the
/// normalizer persists before its first derived write, and the Cleanup Job references the
/// deletion service builds from it. Objects are deleted exactly the way the background cleanup
/// worker does, one idempotent delete per registered reference.
/// </summary>
public sealed class DerivedOutputDeletionInventoryTests
{
    private const string PayloadSha256 =
        "239f59ed55e737c77147cf55ad0c1b030b6d7ee748a7426952f9b852d5a935e5";

    [Theory]
    [InlineData(DeletionTarget.Document)]
    [InlineData(DeletionTarget.ParseRun)]
    public async Task Failed_normalization_outputs_are_fully_removed_by_the_registered_cleanup_refs(
        DeletionTarget target)
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "# Synthetic test"u8.ToArray()),
            (
                "content_list.json",
                """[{"type":"text","text":"Synthetic test","page_idx":-1}]"""u8.ToArray()));
        var cancellationToken = TestContext.Current.CancellationToken;

        // The production normalizer rejects the negative page only after it already stored the
        // derived Markdown, Content List, and manifest.
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(
                new(parseRunId, ProviderTypes.MinerUCloud, archive),
                cancellationToken));
        Assert.Equal("mineru-result-page-invalid", error.ErrorCode);
        Assert.True(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/markdown.md"));
        Assert.True(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/content-list.json"));
        Assert.True(environment.Exists($"parse-runs/{parseRunId:N}/provider/derived-manifest.json"));

        var documentId = await environment.PersistRunAsync(parseRunId, ParseRunStatuses.Failed);
        var (result, refs) = await environment.RequestDeletionAsync(target, parseRunId, documentId);

        Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
        Assert.Contains($"parse-runs/{parseRunId:N}/artifacts/markdown.md", refs);
        Assert.Contains($"parse-runs/{parseRunId:N}/artifacts/content-list.json", refs);
        Assert.Contains($"parse-runs/{parseRunId:N}/provider/derived-manifest.json", refs);
        Assert.Contains($"parse-runs/{parseRunId:N}/provider/result.zip", refs);

        foreach (var storageRef in refs)
        {
            await environment.Storage.DeleteIfExistsAsync(storageRef, cancellationToken);
        }

        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/markdown.md"));
        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/content-list.json"));
        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/provider/derived-manifest.json"));
        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/provider/result.zip"));
    }

    [Fact]
    public async Task Write_failure_after_partial_derived_writes_leaves_a_persisted_deletable_inventory()
    {
        var rootPath = NewRootPath();
        using var environment = await InventoryEnvironment.CreateLocalAsync(rootPath);
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "# Chapter"u8.ToArray()),
            ("content_list.json", """[{"type":"text","text":"Body","page_idx":0}]"""u8.ToArray()),
            ("images/figure.png", PngBytes()));
        var cancellationToken = TestContext.Current.CancellationToken;

        // Write 1 is the manifest, write 2 the derived Markdown; failing write 3 leaves a
        // partial set of derived objects with no committed canonical row.
        var faulting = new FaultInjectionStorage(environment.Storage)
        {
            FailOnWriteNumber = 3,
            Fault = new IOException("synthetic write outage"),
        };
        var faultingNormalizer = environment.CreateNormalizer(faulting);
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            faultingNormalizer.NormalizeAsync(
                new(parseRunId, ProviderTypes.MinerUCloud, archive),
                cancellationToken));
        Assert.Equal("provider-result-normalization-io-failed", error.ErrorCode);
        Assert.True(environment.Exists($"parse-runs/{parseRunId:N}/provider/derived-manifest.json"));
        Assert.True(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/markdown.md"));
        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/content-list.json"));

        // A restart keeps nothing in memory: a fresh service stack over the same storage and a
        // fresh database still deletes every planned output from the persisted manifest.
        using var restarted = await InventoryEnvironment.CreateLocalAsync(rootPath);
        var documentId = await restarted.PersistRunAsync(parseRunId, ParseRunStatuses.Failed);
        var (result, refs) = await restarted.RequestDeletionAsync(
            DeletionTarget.ParseRun,
            parseRunId,
            documentId);
        Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
        foreach (var storageRef in refs)
        {
            await restarted.Storage.DeleteIfExistsAsync(storageRef, cancellationToken);
        }

        Assert.False(restarted.Exists($"parse-runs/{parseRunId:N}/provider/derived-manifest.json"));
        Assert.False(restarted.Exists($"parse-runs/{parseRunId:N}/artifacts/markdown.md"));
        Assert.False(restarted.Exists($"parse-runs/{parseRunId:N}/artifacts/content-list.json"));
        Assert.False(restarted.Exists(AssetStorageKey(parseRunId, "images/figure.png")));
    }

    [Fact]
    public async Task Cancellation_after_partial_derived_writes_leaves_a_persisted_deletable_inventory()
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "# Chapter"u8.ToArray()),
            ("content_list.json", """[{"type":"text","text":"Body","page_idx":0}]"""u8.ToArray()));
        var cancellationToken = TestContext.Current.CancellationToken;

        var faulting = new FaultInjectionStorage(environment.Storage)
        {
            FailOnWriteNumber = 3,
            Fault = new OperationCanceledException(),
        };
        var faultingNormalizer = environment.CreateNormalizer(faulting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            faultingNormalizer.NormalizeAsync(
                new(parseRunId, ProviderTypes.MinerUCloud, archive),
                cancellationToken));

        var documentId = await environment.PersistRunAsync(parseRunId, ParseRunStatuses.Cancelled);
        var (result, refs) = await environment.RequestDeletionAsync(
            DeletionTarget.ParseRun,
            parseRunId,
            documentId);
        Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
        foreach (var storageRef in refs)
        {
            await environment.Storage.DeleteIfExistsAsync(storageRef, cancellationToken);
        }

        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/provider/derived-manifest.json"));
        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/markdown.md"));
    }

    [Fact]
    public async Task Retrying_the_same_archive_after_a_transient_failure_reuses_the_manifest_and_objects()
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var parseRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            parseRunId,
            ("full.md", "# Chapter"u8.ToArray()),
            ("content_list.json", """[{"type":"text","text":"Body","page_idx":0}]"""u8.ToArray()),
            ("images/figure.png", PngBytes()));
        var cancellationToken = TestContext.Current.CancellationToken;

        var faulting = new FaultInjectionStorage(environment.Storage)
        {
            FailOnWriteNumber = 3,
            Fault = new IOException("synthetic write outage"),
        };
        var faultingNormalizer = environment.CreateNormalizer(faulting);
        await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            faultingNormalizer.NormalizeAsync(
                new(parseRunId, ProviderTypes.MinerUCloud, archive),
                cancellationToken));
        var manifestAfterFailure = await environment.ReadAllBytesAsync(
            $"parse-runs/{parseRunId:N}/provider/derived-manifest.json");

        var bundle = await environment.Normalizer.NormalizeAsync(
            new(parseRunId, ProviderTypes.MinerUCloud, archive),
            cancellationToken);

        Assert.True(ParseBundleValidator.Validate(bundle).IsValid);
        Assert.DoesNotContain(
            bundle.Artifacts,
            artifact => artifact.StorageRef.EndsWith(
                "/provider/derived-manifest.json",
                StringComparison.Ordinal));
        Assert.Equal(
            manifestAfterFailure,
            await environment.ReadAllBytesAsync(
                $"parse-runs/{parseRunId:N}/provider/derived-manifest.json"));
    }

    [Fact]
    public async Task Normalization_persists_the_complete_planned_manifest_for_both_layouts()
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        var legacyRunId = Guid.NewGuid();
        var legacyArchive = await environment.StoreArchiveAsync(
            legacyRunId,
            ("full.md", "# Legacy"u8.ToArray()),
            ("content_list.json", """[{"type":"text","text":"Body","page_idx":0}]"""u8.ToArray()),
            ("content_list_v2.json", "[]"u8.ToArray()),
            ("layout.json", "{}"u8.ToArray()),
            ("model.json", "{}"u8.ToArray()),
            ("images/figure.png", PngBytes()));
        var legacyBundle = await environment.Normalizer.NormalizeAsync(
            new(legacyRunId, ProviderTypes.MinerUCloud, legacyArchive),
            cancellationToken);
        await AssertManifestAsync(
            environment,
            legacyRunId,
            legacyArchive,
            [
                $"parse-runs/{legacyRunId:N}/artifacts/markdown.md",
                $"parse-runs/{legacyRunId:N}/artifacts/content-list.json",
                $"parse-runs/{legacyRunId:N}/artifacts/content-list-v2.json",
                $"parse-runs/{legacyRunId:N}/artifacts/layout.json",
                $"parse-runs/{legacyRunId:N}/artifacts/model-output.json",
                AssetStorageKey(legacyRunId, "images/figure.png"),
            ]);
        Assert.DoesNotContain(
            legacyBundle.Artifacts,
            artifact => artifact.StorageRef.EndsWith(
                "/provider/derived-manifest.json",
                StringComparison.Ordinal));

        var structuredRunId = Guid.NewGuid();
        var structuredArchive = await environment.StoreArchiveAsync(
            structuredRunId,
            ("markdown.md", "# V4"u8.ToArray()),
            ("structured_content.json", """{"pages":[{"page_idx":0,"blocks":[]}]}"""u8.ToArray()),
            ("middle_json.json", "{}"u8.ToArray()),
            ("model_output.json", "{}"u8.ToArray()),
            ("images/chart.png", PngBytes()));
        await environment.Normalizer.NormalizeAsync(
            new(structuredRunId, ProviderTypes.MinerULocal, structuredArchive),
            cancellationToken);
        await AssertManifestAsync(
            environment,
            structuredRunId,
            structuredArchive,
            [
                $"parse-runs/{structuredRunId:N}/artifacts/markdown.md",
                $"parse-runs/{structuredRunId:N}/artifacts/structured-content.json",
                $"parse-runs/{structuredRunId:N}/artifacts/layout.json",
                $"parse-runs/{structuredRunId:N}/artifacts/model-output.json",
                AssetStorageKey(structuredRunId, "images/chart.png"),
            ]);
    }

    [Theory]
    [InlineData(DeletionTarget.Document)]
    [InlineData(DeletionTarget.ParseRun)]
    public async Task Segmented_run_deletion_covers_segment_manifests_markdown_and_merged_markdown(
        DeletionTarget target)
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var parseRunId = Guid.NewGuid();
        var segmentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        // Objects a segmented run leaves behind when every segment normalized and the merge ran
        // but the final canonical commit never happened, plus the durable per-segment manifests.
        for (var index = 0; index < segmentIds.Length; index++)
        {
            var segmentId = segmentIds[index];
            await environment.WriteManifestAsync(
                segmentId,
                PayloadSha256,
                [
                    $"parse-runs/{segmentId:N}/artifacts/markdown.md",
                    $"parse-runs/{segmentId:N}/artifacts/content-list.json",
                ]);
            await environment.WriteAsync(
                $"parse-runs/{segmentId:N}/provider/result.zip",
                "segment-archive"u8.ToArray());
            await environment.WriteAsync(
                $"parse-runs/{segmentId:N}/artifacts/markdown.md",
                Encoding.UTF8.GetBytes($"# Segment {index}"));
            await environment.WriteAsync(
                $"parse-runs/{segmentId:N}/artifacts/content-list.json",
                "[]"u8.ToArray());
        }

        await environment.WriteAsync(
            $"parse-runs/{parseRunId:N}/provider/result.zip",
            "parent-archive"u8.ToArray());
        await environment.WriteAsync(
            $"parse-runs/{parseRunId:N}/artifacts/document.md",
            "# Merged"u8.ToArray());
        for (var index = 0; index < segmentIds.Length; index++)
        {
            await environment.WriteAsync(
                $"parse-runs/{parseRunId:N}/segments/{index:D4}.pdf",
                Encoding.UTF8.GetBytes($"segment-{index}"));
        }

        var documentId = await environment.PersistRunAsync(
            parseRunId,
            ParseRunStatuses.Failed,
            segmentIds.Select((id, index) => (id, $"parse-runs/{parseRunId:N}/segments/{index:D4}.pdf")).ToArray());
        var (result, refs) = await environment.RequestDeletionAsync(target, parseRunId, documentId);
        Assert.Equal(ResourceDeletionStatus.Accepted, result.Status);
        Assert.Contains($"parse-runs/{parseRunId:N}/artifacts/document.md", refs);
        foreach (var segmentId in segmentIds)
        {
            Assert.Contains($"parse-runs/{segmentId:N}/provider/derived-manifest.json", refs);
            Assert.Contains($"parse-runs/{segmentId:N}/artifacts/markdown.md", refs);
            Assert.Contains($"parse-runs/{segmentId:N}/artifacts/content-list.json", refs);
        }

        foreach (var storageRef in refs)
        {
            await environment.Storage.DeleteIfExistsAsync(storageRef, cancellationToken);
        }

        Assert.False(environment.Exists($"parse-runs/{parseRunId:N}/artifacts/document.md"));
        foreach (var segmentId in segmentIds)
        {
            Assert.False(environment.Exists($"parse-runs/{segmentId:N}/provider/derived-manifest.json"));
            Assert.False(environment.Exists($"parse-runs/{segmentId:N}/artifacts/markdown.md"));
            Assert.False(environment.Exists($"parse-runs/{segmentId:N}/artifacts/content-list.json"));
        }
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("not-an-object")]
    [InlineData("oversized")]
    [InlineData("wrong-version")]
    [InlineData("identity-mismatch")]
    [InlineData("bad-digest")]
    [InlineData("missing-outputs")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-outputs")]
    [InlineData("too-many-outputs")]
    [InlineData("foreign-run-key")]
    [InlineData("nested-traversal-key")]
    [InlineData("out-of-prefix-key")]
    public async Task Malformed_manifests_refuse_deletion_before_any_lifecycle_mutation(string scenario)
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var parseRunId = Guid.NewGuid();
        var documentId = await environment.PersistRunAsync(parseRunId, ParseRunStatuses.Failed);
        await environment.WriteAsync(
            DerivedOutputManifest.StorageKey(parseRunId),
            MalformedManifest(scenario, parseRunId));
        var cancellationToken = TestContext.Current.CancellationToken;

        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() =>
            environment.RequestDeletionAsync(DeletionTarget.ParseRun, parseRunId, documentId));

        Assert.StartsWith(
            $"The derived-output manifest for Parse Run '{parseRunId:D}' ",
            invalid.Message);
        Assert.DoesNotContain("leaky-marker", invalid.ToString(), StringComparison.Ordinal);
        await using var verification = new StructaDocDbContext(environment.Options);
        Assert.Equal(
            ResourceLifecycleStates.Active,
            await verification.ParseRuns.AsNoTracking()
                .Where(item => item.Id == parseRunId)
                .Select(item => item.LifecycleState)
                .SingleAsync(cancellationToken));
        Assert.Empty(await verification.CleanupJobs.AsNoTracking().ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Storage_outage_during_manifest_read_fails_deletion_without_mutation()
    {
        using var environment = await InventoryEnvironment.CreateLocalAsync();
        var parseRunId = Guid.NewGuid();
        var documentId = await environment.PersistRunAsync(parseRunId, ParseRunStatuses.Failed);
        await environment.WriteManifestAsync(
            parseRunId,
            PayloadSha256,
            [$"parse-runs/{parseRunId:N}/artifacts/markdown.md"]);

        var outage = new ReadFailingStorage(
            environment.Storage,
            storageRef => storageRef.EndsWith("/provider/derived-manifest.json", StringComparison.Ordinal));
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            environment.RequestDeletionAsync(
                DeletionTarget.ParseRun,
                parseRunId,
                documentId,
                outage));

        Assert.Contains("synthetic storage outage", exception.Message);
        await using var verification = new StructaDocDbContext(environment.Options);
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(
            ResourceLifecycleStates.Active,
            await verification.ParseRuns.AsNoTracking()
                .Where(item => item.Id == parseRunId)
                .Select(item => item.LifecycleState)
                .SingleAsync(cancellationToken));
        Assert.Empty(await verification.CleanupJobs.AsNoTracking()
            .ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task S3_deletion_reads_derived_manifests_and_distinguishes_a_missing_legacy_object()
    {
        using var client = new InMemoryS3Client();
        using var environment = await InventoryEnvironment.CreateS3Async(client, NewRootPath());
        var cancellationToken = TestContext.Current.CancellationToken;

        // A current run: the manifest written through the S3 storage enters the inventory.
        var currentRunId = Guid.NewGuid();
        var archive = await environment.StoreArchiveAsync(
            currentRunId,
            ("full.md", "# Synthetic test"u8.ToArray()),
            (
                "content_list.json",
                """[{"type":"text","text":"Synthetic test","page_idx":-1}]"""u8.ToArray()));
        var error = await Assert.ThrowsAsync<ProviderResultNormalizationException>(() =>
            environment.Normalizer.NormalizeAsync(
                new(currentRunId, ProviderTypes.MinerUCloud, archive),
                cancellationToken));
        Assert.Equal("mineru-result-page-invalid", error.ErrorCode);

        var currentDocumentId = await environment.PersistRunAsync(currentRunId, ParseRunStatuses.Failed);
        var (currentResult, currentRefs) = await environment.RequestDeletionAsync(
            DeletionTarget.ParseRun,
            currentRunId,
            currentDocumentId);
        Assert.Equal(ResourceDeletionStatus.Accepted, currentResult.Status);
        Assert.Contains($"parse-runs/{currentRunId:N}/provider/derived-manifest.json", currentRefs);
        Assert.Contains($"parse-runs/{currentRunId:N}/artifacts/markdown.md", currentRefs);
        Assert.Contains($"parse-runs/{currentRunId:N}/artifacts/content-list.json", currentRefs);

        // A legacy run: no manifest object exists, so deletion falls back to committed rows and
        // the deterministic known keys, and still accepts.
        var legacyRunId = Guid.NewGuid();
        var legacyDocumentId = await environment.PersistRunAsync(legacyRunId, ParseRunStatuses.Failed);
        var (legacyResult, legacyRefs) = await environment.RequestDeletionAsync(
            DeletionTarget.ParseRun,
            legacyRunId,
            legacyDocumentId);
        Assert.Equal(ResourceDeletionStatus.Accepted, legacyResult.Status);
        Assert.Contains($"parse-runs/{legacyRunId:N}/provider/derived-manifest.json", legacyRefs);
        Assert.DoesNotContain(
            legacyRefs,
            storageRef => storageRef.StartsWith($"parse-runs/{legacyRunId:N}/artifacts/", StringComparison.Ordinal)
                || storageRef.StartsWith($"parse-runs/{legacyRunId:N}/assets/", StringComparison.Ordinal));

        // An outage is not absence: a server error on the manifest read fails the request
        // without mutating lifecycle state or creating a job.
        var outageRunId = Guid.NewGuid();
        var outageDocumentId = await environment.PersistRunAsync(outageRunId, ParseRunStatuses.Failed);
        await environment.WriteManifestAsync(
            outageRunId,
            PayloadSha256,
            [$"parse-runs/{outageRunId:N}/artifacts/markdown.md"]);
        client.FailReads = true;
        await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            environment.RequestDeletionAsync(DeletionTarget.ParseRun, outageRunId, outageDocumentId));
        client.FailReads = false;
        await using var verification = new StructaDocDbContext(environment.Options);
        Assert.Equal(
            ResourceLifecycleStates.Active,
            await verification.ParseRuns.AsNoTracking()
                .Where(item => item.Id == outageRunId)
                .Select(item => item.LifecycleState)
                .SingleAsync(cancellationToken));
        Assert.Equal(2, await verification.CleanupJobs.AsNoTracking().CountAsync(cancellationToken));
    }

    private static async Task AssertManifestAsync(
        InventoryEnvironment environment,
        Guid parseRunId,
        StoredProviderArchive archive,
        IReadOnlyList<string> expectedOutputs)
    {
        var bytes = await environment.ReadAllBytesAsync(
            $"parse-runs/{parseRunId:N}/provider/derived-manifest.json");
        using var manifest = JsonDocument.Parse(bytes);
        var root = manifest.RootElement;
        Assert.Equal(DerivedOutputManifest.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal(parseRunId.ToString("N"), root.GetProperty("identity").GetString());
        Assert.Equal(archive.Sha256.ToLowerInvariant(), root.GetProperty("sourceArchiveSha256").GetString());
        Assert.Equal(
            expectedOutputs,
            root.GetProperty("outputs").EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    private static byte[] MalformedManifest(string scenario, Guid parseRunId)
    {
        var identity = parseRunId.ToString("N");
        var digest = new string('a', 64);
        var foreignIdentity = Guid.NewGuid().ToString("N");
        var duplicateKey = $"parse-runs/{parseRunId:N}/artifacts/markdown.md";
        var traversalKey = $"parse-runs/{parseRunId:N}/artifacts/../leaky-marker.md";
        var outOfPrefixKey = $"documents/{identity}/leaky-marker.pdf";
        string ValidKey(string name) => $"parse-runs/{parseRunId:N}/artifacts/{name}";
        string Manifest(
            string version,
            string manifestIdentity,
            string sourceArchiveSha256,
            string outputs,
            string trailing = "") => $$"""{"version":{{version}},"identity":"{{manifestIdentity}}","sourceArchiveSha256":"{{sourceArchiveSha256}}","outputs":[{{outputs}}]{{trailing}}}""";
        return scenario switch
        {
            "invalid-json" => "{not-json"u8.ToArray(),
            "not-an-object" => "[]"u8.ToArray(),
            "oversized" => new byte[DerivedOutputManifest.MaxJsonBytes + 2],
            "wrong-version" => Encoding.UTF8.GetBytes(Manifest("2", identity, digest, "")),
            "identity-mismatch" => Encoding.UTF8.GetBytes(Manifest("1", foreignIdentity, digest, "")),
            "bad-digest" => Encoding.UTF8.GetBytes(Manifest("1", identity, "NOT-A-DIGEST", "")),
            "missing-outputs" => Encoding.UTF8.GetBytes(
                $$"""{"version":1,"identity":"{{identity}}","sourceArchiveSha256":"{{digest}}"}"""),
            "unknown-field" => Encoding.UTF8.GetBytes(Manifest("1", identity, digest, "", trailing: ",\"extra\":1")),
            "duplicate-outputs" => Encoding.UTF8.GetBytes(
                Manifest("1", identity, digest, $"""
                    "{duplicateKey}","{duplicateKey}"
                    """)),
            "too-many-outputs" => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                version = 1,
                identity,
                sourceArchiveSha256 = digest,
                outputs = Enumerable.Range(0, DerivedOutputManifest.MaxOutputs + 1)
                    .Select(index => ValidKey($"name-{index:D6}.md"))
                    .ToArray(),
            })),
            "foreign-run-key" => Encoding.UTF8.GetBytes(Manifest(
                "1",
                identity,
                digest,
                $"""
                    "parse-runs/{foreignIdentity}/artifacts/leaky-marker.md"
                    """)),
            "nested-traversal-key" => Encoding.UTF8.GetBytes(Manifest(
                "1",
                identity,
                digest,
                $"""
                    "{traversalKey}"
                    """)),
            "out-of-prefix-key" => Encoding.UTF8.GetBytes(Manifest(
                "1",
                identity,
                digest,
                $"""
                    "{outOfPrefixKey}"
                    """)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    private static string AssetStorageKey(Guid parseRunId, string path) =>
        $"parse-runs/{parseRunId:N}/assets/{Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(path.Normalize(NormalizationForm.FormC))))
            .ToLowerInvariant()[..24]}.png";

    private static string NewRootPath() => Path.Combine(
        Path.GetTempPath(),
        "structadoc-derived-inventory-tests",
        Guid.NewGuid().ToString("N"));

    private static byte[] PngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public enum DeletionTarget
    {
        Document,
        ParseRun,
    }

    /// <summary>
    /// A production intake, normalizer, migrated temporary SQLite database, and storage root.
    /// Creating a second environment over the same root path models a process restart: nothing
    /// is shared except the durable objects and manifests already stored there.
    /// </summary>
    private sealed class InventoryEnvironment(
        IFileStorage storage,
        Func<string, bool> exists,
        DbContextOptions<StructaDocDbContext> options,
        ProviderResultNormalizationOptions normalizationOptions,
        string rootPath) : IDisposable
    {
        public static async Task<InventoryEnvironment> CreateLocalAsync(string? rootPath = null)
        {
            rootPath ??= NewRootPath();
            Directory.CreateDirectory(rootPath);
            var storage = new LocalFileStorage(new FileStorageOptions { RootPath = rootPath });
            return await FinishAsync(
                storage,
                storageRef => File.Exists(Path.Combine(
                    rootPath,
                    storageRef.Replace('/', Path.DirectorySeparatorChar))),
                rootPath);
        }

        public static async Task<InventoryEnvironment> CreateS3Async(InMemoryS3Client client, string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            var storage = new S3FileStorage(
                client,
                new FileStorageOptions { Bucket = "structadoc-tests", Prefix = "inventory" });
            return await FinishAsync(
                storage,
                storageRef => client.Contains($"inventory/{storageRef}"),
                rootPath);
        }

        private static async Task<InventoryEnvironment> FinishAsync(
            IFileStorage storage,
            Func<string, bool> exists,
            string rootPath)
        {
            var optionsBuilder = new DbContextOptionsBuilder<StructaDocDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(rootPath, $"inventory-{Guid.NewGuid():N}.db")};Pooling=False",
                    sqlite => sqlite.MigrationsAssembly(
                        typeof(SqliteDesignTimeDbContextFactory).Assembly));
            var options = optionsBuilder.Options;
            await using var dbContext = new StructaDocDbContext(options);
            await dbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return new InventoryEnvironment(
                storage,
                exists,
                options,
                new ProviderResultNormalizationOptions
                {
                    MaxMarkdownBytes = 1024 * 1024,
                    MaxJsonBytes = 1024 * 1024,
                    MaxAssetBytes = 1024 * 1024,
                    TemporaryPath = Path.Combine(rootPath, "normalizer-temp"),
                },
                rootPath);
        }

        public IFileStorage Storage => storage;

        public ProviderResultNormalizationOptions NormalizationOptions => normalizationOptions;

        public DbContextOptions<StructaDocDbContext> Options => options;

        public StoredProviderResultIntake Intake { get; } = new(
            storage,
            new ProviderResultIntakeOptions
            {
                MaxArchiveBytes = 8 * 1024 * 1024,
                MaxEntryCount = 100,
                MaxEntryBytes = 8 * 1024 * 1024,
                MaxExpandedBytes = 16 * 1024 * 1024,
                MaxCompressionRatio = 1000,
                TemporaryPath = Path.Combine(rootPath, "intake-temp"),
            });

        public MinerUResultNormalizer Normalizer { get; } = new(
            storage,
            normalizationOptions,
            NullLogger<MinerUResultNormalizer>.Instance);

        public bool Exists(string storageRef) => exists(storageRef);

        public MinerUResultNormalizer CreateNormalizer(IFileStorage fileStorage) => new(
            fileStorage,
            normalizationOptions,
            NullLogger<MinerUResultNormalizer>.Instance);

        public async Task<StoredProviderArchive> StoreArchiveAsync(
            Guid parseRunId,
            params (string Path, byte[] Content)[] entries)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in entries)
                {
                    var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
                    await using var entryContent = entry.Open();
                    await entryContent.WriteAsync(content);
                }
            }

            await using var result = new ProviderResultContent(
                new MemoryStream(output.ToArray(), writable: false),
                "application/zip");
            return await Intake.StoreArchiveAsync(parseRunId, result, TestContext.Current.CancellationToken);
        }

        public async Task<Guid> PersistRunAsync(
            Guid parseRunId,
            string status,
            IReadOnlyList<(Guid Id, string StorageRef)>? segments = null)
        {
            await using var dbContext = new StructaDocDbContext(options);
            var documentId = Guid.NewGuid();
            var nowUtc = DateTime.UtcNow;
            dbContext.Documents.Add(new DocumentEntity
            {
                Id = documentId,
                OriginalFileName = "inventory.pdf",
                MediaType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 7,
                Sha256 = PayloadSha256,
                StorageRef = $"documents/{documentId:N}/original.pdf",
                CreatedAtUtc = nowUtc,
            });
            dbContext.ParseRuns.Add(new ParseRunEntity
            {
                Id = parseRunId,
                DocumentId = documentId,
                Status = status,
                ProviderType = ProviderTypes.MinerUCloud,
                ProviderConfigId = Guid.NewGuid(),
                ProviderConfigVersion = Guid.NewGuid(),
                OptionsJson = "{}",
                SourceMediaType = "application/pdf",
                SubmittedMediaType = "application/pdf",
                AttemptCount = 1,
                MaxAttempts = 3,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                CompletedAtUtc = nowUtc,
            });
            for (var index = 0; index < (segments?.Count ?? 0); index++)
            {
                var segment = segments![index];
                dbContext.ParseSegments.Add(new ParseSegmentEntity
                {
                    Id = segment.Id,
                    ParseRunId = parseRunId,
                    Index = index,
                    StartPage = index + 1,
                    EndPage = index + 1,
                    StorageRef = segment.StorageRef,
                    SizeBytes = 7,
                    Sha256 = PayloadSha256,
                    Status = "normalized",
                    UpdatedAtUtc = nowUtc,
                });
            }

            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            return documentId;
        }

        public async Task<(ResourceDeletionResult Result, string[] Refs)> RequestDeletionAsync(
            DeletionTarget target,
            Guid parseRunId,
            Guid documentId,
            IFileStorage? storageOverride = null)
        {
            await using var dbContext = new StructaDocDbContext(options);
            var service = new EfCoreResourceDeletionService(dbContext, storageOverride ?? storage);
            var result = target switch
            {
                DeletionTarget.Document => await service.RequestDocumentDeletionAsync(
                    documentId,
                    ResourceAccessContext.System,
                    DateTime.UtcNow,
                    TestContext.Current.CancellationToken),
                DeletionTarget.ParseRun => await service.RequestParseRunDeletionAsync(
                    parseRunId,
                    ResourceAccessContext.System,
                    DateTime.UtcNow,
                    TestContext.Current.CancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };

            if (result.Status != ResourceDeletionStatus.Accepted)
            {
                return (result, []);
            }

            await using var verification = new StructaDocDbContext(options);
            var refsJson = await verification.CleanupJobs
                .AsNoTracking()
                .SingleAsync(
                    job => job.TargetId == parseRunId || job.TargetId == documentId,
                    TestContext.Current.CancellationToken);
            return (result, JsonSerializer.Deserialize<string[]>(refsJson.StorageRefsJson)!);
        }

        public async Task WriteAsync(string storageRef, byte[] content)
        {
            await using var input = new MemoryStream(content, writable: false);
            await storage.WriteAsync(
                storageRef,
                input,
                Math.Max(content.Length, 1),
                TestContext.Current.CancellationToken);
        }

        public async Task WriteManifestAsync(
            Guid identity,
            string sourceArchiveSha256,
            IReadOnlyList<string> outputs)
        {
            var json = DerivedOutputManifest.Serialize(identity, sourceArchiveSha256, outputs);
            await WriteAsync(
                DerivedOutputManifest.StorageKey(identity),
                Encoding.UTF8.GetBytes(json));
        }

        public async Task<byte[]> ReadAllBytesAsync(string storageRef)
        {
            await using var stream = await storage.OpenReadAsync(
                storageRef,
                TestContext.Current.CancellationToken);
            using var content = new MemoryStream();
            await stream.CopyToAsync(content, TestContext.Current.CancellationToken);
            return content.ToArray();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // A restarted environment sharing this root may have removed it first.
            }
            catch (IOException)
            {
                // A restarted environment sharing this root may have removed it first.
            }
        }
    }

    private sealed class FaultInjectionStorage(IFileStorage inner) : IFileStorage
    {
        private long writeCount;

        public long FailOnWriteNumber { get; init; }

        public Exception Fault { get; init; } = new IOException("synthetic write outage");

        public async Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writeCount) == FailOnWriteNumber)
            {
                throw Fault;
            }

            return await inner.WriteAsync(storageRef, content, maxBytes, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(storageRef, cancellationToken);

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfExistsAsync(storageRef, cancellationToken);
    }

    private sealed class ReadFailingStorage(IFileStorage inner, Func<string, bool> shouldFail) : IFileStorage
    {
        public Task<StoredFile> WriteAsync(
            string storageRef,
            Stream content,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(storageRef, content, maxBytes, cancellationToken);

        public Task<Stream> OpenReadAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            shouldFail(storageRef)
                ? Task.FromException<Stream>(new IOException("synthetic storage outage"))
                : inner.OpenReadAsync(storageRef, cancellationToken);

        public Task DeleteIfExistsAsync(
            string storageRef,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfExistsAsync(storageRef, cancellationToken);
    }
}
