using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StructaDoc.Host.OpenApi;

// Machine-contract details which are real HTTP behavior but cannot be inferred from handlers that
// deliberately read headers and multipart forms through HttpContext.
internal sealed class ApiClientContractTransformer : IOpenApiOperationTransformer
{
    private const int MaximumIdempotencyKeyLength = 256;

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        // ApiDescription retains route constraints while the serialized OpenAPI path does not.
        var relativePath = context.Description.RelativePath?.Split('?', 2)[0] ?? string.Empty;
        var path = $"/{relativePath}".Replace(":guid", string.Empty, StringComparison.Ordinal).TrimEnd('/');
        var method = context.Description.HttpMethod;

        if (method == HttpMethods.Post && path == "/api/v1/documents")
        {
            DescribeUpload(operation);
        }

        if (method == HttpMethods.Post
            && path == "/api/v1/documents/{documentId}/parse-runs")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "An optional visible-ASCII key. Reusing it for the same Document and request replays the original Parse Run instead of creating duplicate work.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    MaxLength = MaximumIdempotencyKeyLength,
                },
            });
        }

        DescribePagination(operation, path, method);
        DescribeConditionalAndRangeResponses(operation, path, method);
        return Task.CompletedTask;
    }

    private static void DescribeUpload(OpenApiOperation operation)
    {
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "One supported document in the `file` form field.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                        {
                            ["file"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "binary",
                                Description = "The document bytes and original filename.",
                            },
                        },
                        Required = new HashSet<string>(StringComparer.Ordinal) { "file" },
                        AdditionalPropertiesAllowed = false,
                    },
                },
            },
        };
    }

    private static void DescribePagination(OpenApiOperation operation, string path, string? method)
    {
        if (method != HttpMethods.Get || operation.Parameters is null)
        {
            return;
        }

        if (path == "/api/v1/documents")
        {
            ConfigureIntegerParameter(operation, "limit", 1, 200, 50, "Maximum number of Documents to return.");
            ConfigureParameter(operation, "cursor", "Opaque cursor returned as `nextCursor` by the previous page.");
            ConfigureParameter(operation, "fileName", "Case-insensitive filename filter.");
            ConfigureParameter(operation, "parseStatus", "Latest Parse Run status filter.");
        }
        else if (path == "/api/v1/parse-runs/{parseRunId}/blocks")
        {
            ConfigureIntegerParameter(operation, "limit", 1, 1000, 200, "Maximum number of Blocks to return.");
            ConfigureIntegerParameter(operation, "afterSequence", 0, null, null, "Return Blocks whose sequence is greater than this cursor.");
            ConfigureIntegerParameter(operation, "pageNumber", 1, null, null, "Return only Blocks on this one-based page number.");
        }
    }

    private static void DescribeConditionalAndRangeResponses(
        OpenApiOperation operation,
        string path,
        string? method)
    {
        if (method != HttpMethods.Get)
        {
            return;
        }

        var isDownload = path is "/api/v1/documents/{id}/content"
            or "/api/v1/parse-runs/{parseRunId}/assets/{assetId}/content"
            or "/api/v1/parse-runs/{parseRunId}/artifacts/{artifactId}/content"
            or "/api/v1/parse-runs/{parseRunId}/markdown"
            or "/api/v1/parse-runs/{parseRunId}/markdown/preview"
            or "/api/v1/parse-runs/{parseRunId}/exports/{format}";

        if (isDownload && operation.Responses?.TryGetValue("200", out var success) == true)
        {
            operation.Responses.TryAdd("206", new OpenApiResponse
            {
                Description = "Partial content selected by the Range header.",
                Content = success.Content,
                Headers = success.Headers,
            });
            operation.Responses.TryAdd("416", new OpenApiResponse
            {
                Description = "The requested byte range cannot be satisfied.",
            });
        }

        if (path == "/api/v1/parse-runs/{parseRunId}/markdown/preview")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-None-Match",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Return 304 when this entity tag still identifies the rendered preview.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
            operation.Responses?.TryAdd("304", new OpenApiResponse
            {
                Description = "The rendered preview has not changed.",
            });
        }
    }

    private static void ConfigureIntegerParameter(
        OpenApiOperation operation,
        string name,
        int minimum,
        int? maximum,
        int? defaultValue,
        string description)
    {
        var parameter = operation.Parameters?.FirstOrDefault(candidate => candidate.Name == name);
        if (parameter is not OpenApiParameter concreteParameter)
        {
            return;
        }

        concreteParameter.Description = description;
        concreteParameter.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Format = "int32",
            Minimum = minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Maximum = maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Default = defaultValue is null ? null : JsonValue.Create(defaultValue.Value),
        };
    }

    private static void ConfigureParameter(OpenApiOperation operation, string name, string description)
    {
        var parameter = operation.Parameters?.FirstOrDefault(candidate => candidate.Name == name);
        if (parameter is not null)
        {
            parameter.Description = description;
        }
    }
}
