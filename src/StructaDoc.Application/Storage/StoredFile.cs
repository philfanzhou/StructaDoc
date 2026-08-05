namespace StructaDoc.Application.Storage;

public sealed record StoredFile(
    string StorageRef,
    long SizeBytes,
    string Sha256);
