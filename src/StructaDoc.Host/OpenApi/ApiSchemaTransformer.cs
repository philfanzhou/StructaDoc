using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using StructaDoc.Contracts.ParseRuns;

namespace StructaDoc.Host.OpenApi;

internal sealed class ApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(JsonElement))
        {
            // Provider-neutral options are intentionally extensible, but they are always objects;
            // an empty schema says they may also be a scalar or array and generates an `any` type.
            schema.Type = JsonSchemaType.Object;
            schema.AdditionalPropertiesAllowed = true;
        }

        if (context.JsonTypeInfo.Type == typeof(ParseRunCreateRequest))
        {
            if (schema.Properties?.TryGetValue("options", out var options) == true
                && options is OpenApiSchema optionsSchema)
            {
                optionsSchema.Type = JsonSchemaType.Object;
                optionsSchema.AdditionalPropertiesAllowed = true;
            }

            if (schema.Properties?.TryGetValue("maxAttempts", out var maxAttempts) == true
                && maxAttempts is OpenApiSchema maxAttemptsSchema)
            {
                maxAttemptsSchema.Minimum = "1";
                maxAttemptsSchema.Maximum = "10";
                maxAttemptsSchema.Default = 3;
                maxAttemptsSchema.Type = JsonSchemaType.Integer;
                maxAttemptsSchema.Pattern = null;
            }
        }

        if (context.JsonTypeInfo.Type == typeof(ParseRunResponse)
            && schema.Properties?.TryGetValue("options", out var responseOptions) == true
            && responseOptions is OpenApiSchema responseOptionsSchema)
        {
            responseOptionsSchema.Type = JsonSchemaType.Object;
            responseOptionsSchema.AdditionalPropertiesAllowed = true;
        }

        return Task.CompletedTask;
    }
}
