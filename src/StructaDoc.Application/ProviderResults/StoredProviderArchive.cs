namespace StructaDoc.Application.ProviderResults;

public sealed record StoredProviderArchive(
    string Name,
    string MediaType,
    string StorageRef,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<ProviderArchiveEntry> Entries,
    long ExpandedSizeBytes);

public sealed record ProviderArchiveEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    long CompressedSizeBytes);
