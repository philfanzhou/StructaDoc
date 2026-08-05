using System.Text.Json;
using StructaDoc.Application.Providers;

namespace StructaDoc.Infrastructure.Providers;

internal sealed record MinerUProviderOptions(
    bool? Ocr,
    bool Formula,
    bool Table,
    string Language,
    string? ParseMethod,
    string? Effort,
    bool? ImageAnalysis,
    int? StartPage,
    int? EndPage)
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "ocr",
        "formula",
        "table",
        "language",
        "parseMethod",
        "effort",
        "imageAnalysis",
        "startPage",
        "endPage",
    };

    public static MinerUProviderOptions Parse(string optionsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsJson);

        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidOptions("Provider options must be a JSON object.");
            }

            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!KnownProperties.Contains(property.Name))
                {
                    throw InvalidOptions(
                        $"Provider option '{property.Name}' is not supported.");
                }

                if (!seenProperties.Add(property.Name))
                {
                    throw InvalidOptions(
                        $"Provider option '{property.Name}' is duplicated.");
                }
            }

            var ocr = ReadOptionalBoolean(root, "ocr");
            var formula = ReadOptionalBoolean(root, "formula") ?? true;
            var table = ReadOptionalBoolean(root, "table") ?? true;
            var language = ReadOptionalString(root, "language", 32) ?? "ch";
            var parseMethod = ReadOptionalString(root, "parseMethod", 16);
            var effort = ReadOptionalString(root, "effort", 16);
            var imageAnalysis = ReadOptionalBoolean(root, "imageAnalysis");
            var startPage = ReadOptionalInteger(root, "startPage");
            var endPage = ReadOptionalInteger(root, "endPage");

            if (parseMethod is not null
                && parseMethod is not ("auto" or "txt" or "ocr"))
            {
                throw InvalidOptions(
                    "Provider option 'parseMethod' must be 'auto', 'txt', or 'ocr'.");
            }

            if (effort is not null && effort is not ("medium" or "high"))
            {
                throw InvalidOptions(
                    "Provider option 'effort' must be 'medium' or 'high'.");
            }

            if (startPage is < 0 || endPage is < 0)
            {
                throw InvalidOptions("Provider page indexes cannot be negative.");
            }

            if (startPage.HasValue && endPage.HasValue && endPage < startPage)
            {
                throw InvalidOptions(
                    "Provider option 'endPage' cannot be less than 'startPage'.");
            }

            return new MinerUProviderOptions(
                ocr,
                formula,
                table,
                language,
                parseMethod,
                effort,
                imageAnalysis,
                startPage,
                endPage);
        }
        catch (JsonException exception)
        {
            throw new ProviderException(
                "mineru-options-json-invalid",
                "The MinerU Provider options are not valid JSON.",
                ProviderFailureCategory.Input,
                exception);
        }
    }

    public void ValidateForCloud()
    {
        if (ParseMethod is not null || Effort is not null || ImageAnalysis.HasValue)
        {
            throw InvalidOptions(
                "Options 'parseMethod', 'effort', and 'imageAnalysis' are only supported by MinerU Local.");
        }
    }

    private static bool? ReadOptionalBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidOptions($"Provider option '{name}' must be a boolean."),
        };
    }

    private static int? ReadOptionalInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw InvalidOptions($"Provider option '{name}' must be an integer.");
        }

        return result;
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string name,
        int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidOptions($"Provider option '{name}' must be a string.");
        }

        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result)
            || result.Length > maximumLength
            || !string.Equals(result, result.Trim(), StringComparison.Ordinal)
            || result.Any(char.IsControl))
        {
            throw InvalidOptions($"Provider option '{name}' is invalid.");
        }

        return result;
    }

    private static ProviderException InvalidOptions(string message) => new(
        "mineru-options-invalid",
        message,
        ProviderFailureCategory.Input);
}
