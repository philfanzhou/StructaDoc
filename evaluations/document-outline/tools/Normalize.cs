#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../../src/StructaDoc.Adapters/StructaDoc.Adapters.csproj
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StructaDoc.Adapters.ProviderResults;
using StructaDoc.Adapters.Storage;
using StructaDoc.Application.ProviderResults;
using StructaDoc.Application.Providers;
using StructaDoc.Application.Storage;

// Execute current production intake and normalization on synthetic content lists.
// Usage: dotnet run Normalize.cs -- input.zip output-blocks.json task-storage-root
var storage = new LocalFileStorage(new FileStorageOptions { RootPath = args[2] });
var intake = new StoredProviderResultIntake(storage, new ProviderResultIntakeOptions());
var id = Guid.Parse("00000000-0000-0000-0000-000000000162");
await using var input = File.OpenRead(args[0]);
await using var content = new ProviderResultContent(input, "application/zip");
var archive = await intake.StoreArchiveAsync(id, content);
var normalizer = new MinerUResultNormalizer(storage, new ProviderResultNormalizationOptions(), NullLogger<MinerUResultNormalizer>.Instance);
var bundle = await normalizer.NormalizeAsync(new(id, ProviderTypes.MinerULocal, archive));
await using var output = File.Create(args[1]);
using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
writer.WriteStartArray();
foreach (var block in bundle.Blocks)
{
    writer.WriteStartObject();
    writer.WriteNumber("sequence", block.Sequence);
    if (block.PageNumber is { } page)
    {
        writer.WriteNumber("page", page);
    }
    else
    {
        writer.WriteNull("page");
    }
    writer.WriteString("type", block.Type);
    writer.WriteString("subtype", block.Subtype);
    writer.WriteString("content", block.Content);
    writer.WriteEndObject();
}
writer.WriteEndArray();
await writer.FlushAsync();
