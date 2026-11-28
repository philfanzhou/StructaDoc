namespace StructaDoc.Application.ProviderResults;

public sealed record StoredArchive(
    string Name,
    string MediaType,
    string StorageRef,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<ArchiveEntry> Entries,
    long ExpandedSizeBytes);

public sealed record ArchiveEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    long CompressedSizeBytes);
