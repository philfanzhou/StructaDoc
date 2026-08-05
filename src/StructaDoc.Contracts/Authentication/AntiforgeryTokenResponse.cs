namespace StructaDoc.Contracts.Authentication;

public sealed record AntiforgeryTokenResponse(
    string RequestToken,
    string HeaderName);
