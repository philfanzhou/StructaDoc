namespace StructaDoc.Contracts.Documents;

public sealed record DocumentAccessGrantRequest(string Issuer, string Subject, IReadOnlyList<string> Permissions);

public sealed record DocumentAccessGrantResponse(Guid Id, Guid DocumentId, string Issuer, string Subject, IReadOnlyList<string> Permissions, string CreatedBy, DateTime CreatedAt);
