using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Adapters.Persistence.Entities;
using StructaDoc.Application.Canonical;
using StructaDoc.Application.ParseRuns;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;
using StructaDoc.Domain.ParseRuns;
using StructaDoc.Host.ParseRuns;

namespace StructaDoc.Host.Workers;

public sealed class LargePdfParseOrchestrator(
    StructaDocDbContext dbContext,
    IFileStorage storage,
    IProviderResultIntake resultIntake,
    IProviderSubmissionProtector submissionProtector,
    TimeProvider clock)
{
    public async Task<bool> RequiresSegmentationAsync(ProviderDocumentSource source, ProviderCapabilities capabilities, CancellationToken cancellationToken)
    {
        if (!string.Equals(source.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase)) return false;
        if (capabilities.MaxFileBytes.HasValue && source.SizeBytes > capabilities.MaxFileBytes.Value) return true;
        if (!capabilities.MaxPages.HasValue) return false;
        await using var input = await SeekableCopyAsync(await source.OpenReadAsync(cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        cancellationToken.ThrowIfCancellationRequested();
        return pdf.PageCount > capabilities.MaxPages.Value;
    }

    public async Task<ParseBundle> ExecuteAsync(
        ParseRunLeaseSession session,
        ParseRunExecutionContext context,
        ProviderDocumentSource source,
        ProviderCapabilities capabilities,
        IParseProvider provider,
        IProviderResultNormalizer normalizer,
        CancellationToken cancellationToken)
    {
        if (await session.TryUpdateStageAsync(ParseRunStages.Segmenting, cancellationToken) is null) throw new OperationCanceledException(cancellationToken);
        var segments = await EnsureSegmentsAsync(
            session,
            context.ParseRunId,
            source,
            capabilities,
            cancellationToken);
        var bundles = new List<(ParseSegmentEntity Segment, ParseBundle Bundle)>(segments.Count);
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = await resultIntake.TryLoadArchiveAsync(segment.Id, session.ExecutionCancellationToken);
            if (archive is null)
            {
                var checkpoint = segment.ProtectedSubmissionContinuation is null ? null : new ProviderSubmissionCheckpoint(segment.ExternalTaskId!, submissionProtector.Unprotect(segment.ProtectedSubmissionContinuation));
                if (segment.ExternalTaskId is null)
                {
                    checkpoint = await provider.PrepareSubmissionAsync(context.ProviderConfiguration, segment.Id, Source(segment), context.OptionsJson, session.ExecutionCancellationToken);
                    if (checkpoint is not null)
                    {
                        segment.ExternalTaskId = checkpoint.ExternalTaskId;
                        segment.ProtectedSubmissionContinuation = submissionProtector.Protect(checkpoint.ContinuationToken);
                        segment.Status = "submission-prepared";
                        await SaveSegmentAsync(session, segment, cancellationToken);
                    }
                }

                if (segment.ExternalTaskId is null || checkpoint is not null)
                {
                    ProviderSubmission submission;
                    try { submission = await provider.SubmitAsync(context.ProviderConfiguration, segment.Id, Source(segment), context.OptionsJson, checkpoint, session.ExecutionCancellationToken); }
                    catch (ProviderException exception) when (checkpoint is null && exception.Retryable) { throw new ProviderException("provider-segment-submission-outcome-unknown", "A PDF segment submission outcome is unknown and cannot be retried safely.", ProviderFailureCategory.Permanent, exception); }
                    if (checkpoint is not null && submission.ExternalTaskId != checkpoint.ExternalTaskId) throw new ProviderException("provider-segment-submission-id-mismatch", "A PDF segment submission ID did not match its durable checkpoint.", ProviderFailureCategory.Permanent);
                    segment.ExternalTaskId = submission.ExternalTaskId;
                    segment.ProtectedSubmissionContinuation = null;
                    segment.Status = "submitted";
                    await SaveSegmentAsync(session, segment, cancellationToken);
                }

                await WaitAsync(provider, context.ProviderConfiguration, segment.ExternalTaskId!, session.ExecutionCancellationToken);
                await using var result = await provider.OpenResultAsync(context.ProviderConfiguration, segment.ExternalTaskId!, session.ExecutionCancellationToken);
                archive = await resultIntake.StoreArchiveAsync(segment.Id, result, session.ExecutionCancellationToken);
                segment.Status = "downloaded";
                await SaveSegmentAsync(session, segment, cancellationToken);
            }

            var bundle = await normalizer.NormalizeAsync(new ProviderResultNormalizationRequest(segment.Id, context.ProviderConfiguration.ProviderType, archive, context.ProviderConfiguration.Model, context.ProviderConfiguration.Backend), session.ExecutionCancellationToken);
            segment.Status = "normalized";
            await SaveSegmentAsync(session, segment, cancellationToken);
            bundles.Add((segment, bundle));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await MergeAsync(context.ParseRunId, bundles, cancellationToken);
    }

    private async Task<IReadOnlyList<ParseSegmentEntity>> EnsureSegmentsAsync(
        ParseRunLeaseSession session,
        Guid parseRunId,
        ProviderDocumentSource source,
        ProviderCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.ParseSegments.Where(item => item.ParseRunId == parseRunId).OrderBy(item => item.Index).ToListAsync(cancellationToken);
        var existingByIndex = existing.ToDictionary(segment => segment.Index);
        await using var input = await SeekableCopyAsync(await source.OpenReadAsync(cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        cancellationToken.ThrowIfCancellationRequested();
        if (pdf.PageCount == 0) throw InputFailure("large-pdf-empty", "The PDF contains no pages.");
        var maxPages = capabilities.MaxPages ?? 25;
        var ranges = new Queue<(int Start, int Count)>();
        for (var start = 0; start < pdf.PageCount; start += maxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ranges.Enqueue((start, Math.Min(maxPages, pdf.PageCount - start)));
        }
        var created = new List<ParseSegmentEntity>();
        while (ranges.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = ranges.Dequeue();
            await using var chunk = CreateChunk(pdf, parseRunId, range.Start, range.Count);
            cancellationToken.ThrowIfCancellationRequested();
            if (capabilities.MaxFileBytes.HasValue && chunk.Length > capabilities.MaxFileBytes.Value)
            {
                if (range.Count == 1) throw InputFailure("provider-pdf-page-too-large", "A single PDF page exceeds the Provider file size limit.");
                var first = range.Count / 2; ranges.Enqueue((range.Start, first)); ranges.Enqueue((range.Start + first, range.Count - first)); continue;
            }
            var index = created.Count;
            var id = DeterministicId(parseRunId, $"segment:{index}:{range.Start}:{range.Count}");
            var storageRef = $"parse-runs/{parseRunId:N}/segments/{index:D4}.pdf";
            var sizeBytes = chunk.Length;
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(chunk, cancellationToken)).ToLowerInvariant();
            chunk.Position = 0;
            if (!existingByIndex.TryGetValue(index, out var segment))
            {
                segment = new ParseSegmentEntity
                {
                    Id = id,
                    ParseRunId = parseRunId,
                    Index = index,
                    StartPage = range.Start + 1,
                    EndPage = range.Start + range.Count,
                    StorageRef = storageRef,
                    SizeBytes = sizeBytes,
                    Sha256 = sha256,
                    Status = "creating",
                    UpdatedAtUtc = clock.GetUtcNow().UtcDateTime,
                };
                var intent = new ParseSegmentCreation(
                    segment.Id,
                    segment.Index,
                    segment.StartPage,
                    segment.EndPage,
                    segment.StorageRef,
                    segment.SizeBytes,
                    segment.Sha256,
                    segment.Status);
                if (await session.TryCreateSegmentsAsync([intent], cancellationToken) is null)
                {
                    throw new OperationCanceledException(session.ExecutionCancellationToken);
                }
            }
            else if (segment.Id != id
                || segment.StartPage != range.Start + 1
                || segment.EndPage != range.Start + range.Count
                || !string.Equals(segment.StorageRef, storageRef, StringComparison.Ordinal))
            {
                throw SegmentConflict("The persisted Parse Segment does not match the deterministic identity or page range.");
            }
            else if (!string.Equals(segment.Status, "creating", StringComparison.Ordinal))
            {
                created.Add(segment);
                continue;
            }
            else if (segment.SizeBytes != sizeBytes
                || !string.Equals(segment.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw SegmentConflict($"The persisted Parse Segment intent expects {segment.SizeBytes} bytes with SHA-256 {segment.Sha256}, but the source regenerated {sizeBytes} bytes with SHA-256 {sha256}.");
            }

            try
            {
                var stored = await storage.WriteAsync(
                    storageRef,
                    chunk,
                    capabilities.MaxFileBytes ?? Math.Max(source.SizeBytes * 2, 1024 * 1024),
                    cancellationToken);
                if (stored.SizeBytes != sizeBytes
                    || !string.Equals(stored.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw SegmentConflict("The stored Parse Segment does not match its durable intent.");
                }
            }
            catch (StorageObjectConflictException exception)
            {
                throw SegmentConflict("The Parse Segment storage key contains different content.", exception);
            }

            if (segment.Status == "creating")
            {
                segment.Status = "created";
                await SaveSegmentAsync(session, segment, cancellationToken);
            }
            created.Add(segment);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (existingByIndex.Keys.Any(index => index >= created.Count))
        {
            throw SegmentConflict("Persisted Parse Segment intents do not match the source page ranges.");
        }
        return created;
    }

    private async Task<ParseBundle> MergeAsync(Guid parseRunId, IReadOnlyList<(ParseSegmentEntity Segment, ParseBundle Bundle)> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = new List<ParsePage>(); var blocks = new List<ParseBlock>(); var assets = new List<ParseAsset>(); var artifacts = new List<ParseArtifact>(); var markdownArtifacts = new List<(ParseArtifact Artifact, IReadOnlyDictionary<string, ParseAssetRecord> Assets, int SegmentIndex)>();
        foreach (var (segment, bundle) in items.OrderBy(item => item.Segment.StartPage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = segment.StartPage - 1;
            pages.AddRange(bundle.Pages.Select(page => page with { Number = page.Number + offset }));
            blocks.AddRange(bundle.Blocks.Select(block => block with { Sequence = blocks.Count, PageNumber = block.PageNumber + offset }));
            assets.AddRange(bundle.Assets.Select(asset => asset with { Name = $"segment-{segment.Index:D4}-{asset.Name}" }));
            var segmentAssets = ExportAssetLinkRewriter.BuildAssetsByFileName(
                bundle.Assets.Select(asset => new ParseAssetRecord(
                    asset.Id,
                    asset.Name,
                    asset.MediaType,
                    asset.SizeBytes,
                    asset.Sha256,
                    asset.Width,
                    asset.Height)).ToArray());
            markdownArtifacts.AddRange(bundle.Artifacts
                .Where(artifact => artifact.Type == ArtifactTypes.Markdown)
                .Select(artifact => (artifact, segmentAssets, segment.Index)));
            artifacts.AddRange(bundle.Artifacts.Where(artifact => artifact.Type != ArtifactTypes.Markdown).Select(artifact => artifact with { Name = $"segment-{segment.Index:D4}-{artifact.Name}" }));
            artifacts.Add(new ParseArtifact(DeterministicId(parseRunId, $"source-segment:{segment.Index}"), ArtifactTypes.SourceSegment, $"segment-{segment.Index:D4}.pdf", "application/pdf", segment.SizeBytes, segment.Sha256, segment.StorageRef, JsonSerializer.Serialize(new { segment.StartPage, segment.EndPage })));
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (markdownArtifacts.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(Path.GetTempPath(), $"structadoc-merge-{Guid.NewGuid():N}.md");
            try
            {
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
                {
                    for (var index = 0; index < markdownArtifacts.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (index > 0) await writer.WriteAsync("\n\n---\n\n".AsMemory(), cancellationToken);
                        var markdownArtifact = markdownArtifacts[index];
                        await using var input = await storage.OpenReadAsync(
                            markdownArtifact.Artifact.StorageRef,
                            cancellationToken);
                        using var reader = new StreamReader(input, Encoding.UTF8, true, leaveOpen: false);
                        var markdown = await reader.ReadToEndAsync(cancellationToken);
                        var rewritten = ExportAssetLinkRewriter.RewriteSegmentImages(
                            markdown,
                            markdownArtifact.Assets,
                            markdownArtifact.SegmentIndex);
                        await writer.WriteAsync(rewritten.AsMemory(), cancellationToken);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                await using var merged = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var stored = await storage.WriteAsync($"parse-runs/{parseRunId:N}/artifacts/document.md", merged, Math.Max(merged.Length, 1), cancellationToken);
                artifacts.Add(new ParseArtifact(DeterministicId(parseRunId, "artifact:markdown:document.md"), ArtifactTypes.Markdown, "document.md", "text/markdown", stored.SizeBytes, stored.Sha256, stored.StorageRef));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = JsonSerializer.Serialize(new { segmented = true, segments = items.Select(item => new { item.Segment.Index, item.Segment.StartPage, item.Segment.EndPage, providerMetadata = item.Bundle.ProviderMetadataJson }) });
        cancellationToken.ThrowIfCancellationRequested();
        var mergedBundle = new ParseBundle(ParseBundleValidator.CurrentSchemaVersion, parseRunId, pages.OrderBy(page => page.Number).ToArray(), blocks, assets, artifacts, metadata);
        cancellationToken.ThrowIfCancellationRequested();
        return mergedBundle;
    }

    private static async Task SaveSegmentAsync(
        ParseRunLeaseSession session,
        ParseSegmentEntity segment,
        CancellationToken cancellationToken)
    {
        if (await session.TryUpdateSegmentCheckpointAsync(
                new ParseSegmentCheckpoint(
                    segment.Id,
                    segment.Status,
                    segment.ExternalTaskId,
                    segment.ProtectedSubmissionContinuation),
                cancellationToken) is null)
        {
            throw new OperationCanceledException(session.ExecutionCancellationToken);
        }
    }
    private ProviderDocumentSource Source(ParseSegmentEntity segment) => new($"segment-{segment.Index:D4}.pdf", "application/pdf", segment.SizeBytes, token => storage.OpenReadAsync(segment.StorageRef, token));
    private static async Task WaitAsync(IParseProvider provider, ProviderExecutionConfiguration config, string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = await provider.GetStatusAsync(config, id, cancellationToken);
            if (status.State == ProviderTaskState.Succeeded) return;
            if (status.State == ProviderTaskState.Failed) throw new ProviderException(status.ErrorCode ?? "provider-segment-failed", status.ErrorMessage ?? "A PDF segment failed.", status.Retryable ? ProviderFailureCategory.Transient : ProviderFailureCategory.Permanent);
            await Task.Delay(status.SuggestedPollDelay ?? TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static FileStream CreateChunk(
        PdfDocument source,
        Guid parseRunId,
        int start,
        int count)
    {
        var path = Path.Combine(Path.GetTempPath(), $"structadoc-segment-{Guid.NewGuid():N}.pdf");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        using var output = new PdfDocument();
        for (var index = start; index < start + count; index++) output.AddPage(source.Pages[index]);
        output.Info.CreationDate = DateTime.UnixEpoch;
        output.Info.ModificationDate = DateTime.UnixEpoch;
        var documentIdBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{parseRunId:N}:segment-pdf:{start}:{count}"));
        var documentId = Convert.ToHexString(documentIdBytes.AsSpan(0, 16));
        output.Internals.FirstDocumentID = documentId;
        output.Internals.SecondDocumentID = documentId;
        output.Save(stream, closeStream: false);
        NormalizeGeneratedXmpIdentifiers(
            stream,
            new Guid(documentIdBytes.AsSpan(0, 16)).ToString("D"));
        stream.Position = 0;
        return stream;
    }

    private static void NormalizeGeneratedXmpIdentifiers(FileStream stream, string stableUuid)
    {
        stream.Position = 0;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var uuidBytes = Encoding.ASCII.GetBytes(stableUuid);
        NormalizeXmpUuid(bytes, "<xmpMM:DocumentID>uuid:"u8, uuidBytes);
        NormalizeXmpUuid(bytes, "<xmpMM:InstanceID>uuid:"u8, uuidBytes);
        stream.Position = 0;
        stream.Write(bytes);
    }

    private static void NormalizeXmpUuid(
        byte[] bytes,
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> stableUuid)
    {
        var offset = bytes.AsSpan().IndexOf(prefix);
        if (offset < 0) return;
        var valueStart = offset + prefix.Length;
        if (valueStart + stableUuid.Length > bytes.Length) return;
        stableUuid.CopyTo(bytes.AsSpan(valueStart));
    }

    private static async Task<FileStream> SeekableCopyAsync(Stream source, CancellationToken cancellationToken)
    {
        await using (source)
        {
            var path = Path.Combine(Path.GetTempPath(), $"structadoc-pdf-{Guid.NewGuid():N}.pdf");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            try { await source.CopyToAsync(stream, cancellationToken); stream.Position = 0; return stream; } catch { await stream.DisposeAsync(); throw; }
        }
    }

    private static Guid DeterministicId(Guid parent, string value) { var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{parent:N}:{value}")); return new Guid(bytes.AsSpan(0, 16)); }
    private static ProviderException InputFailure(string code, string message) => new(code, message, ProviderFailureCategory.Input);
    private static ProviderException SegmentConflict(string message, Exception? inner = null) => new("parse-segment-object-conflict", message, ProviderFailureCategory.Permanent, inner);
}
