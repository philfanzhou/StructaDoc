namespace StructaDoc.Application.Documents;

public sealed class UnsupportedDocumentTypeException()
    : Exception("The uploaded file is not a supported PDF or Office document.");
