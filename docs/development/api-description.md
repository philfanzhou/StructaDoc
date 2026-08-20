# API Description

StructaDoc is integrated with by other systems. Until now the only description of its API was prose in this repository: it could not be handed to a code generator, could not be diffed when an endpoint changed, and was not open in front of anyone writing a request. The service now publishes an OpenAPI 3.1 document and a page that browses it.

## Where It Is

| Route | What it serves |
|---|---|
| `GET /api/v1/openapi.json` | the OpenAPI 3.1 consumer contract used for SDK generation |
| `GET /api/v1-browser/openapi.json` | the complete browser and service surface for operators |
| `/api/v1/docs` | a browsable page rendering both documents |

All live under `/api` so the descriptions travel with the API they describe, including through a reverse proxy that publishes only that prefix, and so the Host's client-route fallback already excludes them from the application shell.

`v1` in the path is the contract version, not the build. A build that adds an optional field does not change what a client is coding against; `GET /api/v1/system/info` reports which build is answering.

## What It Describes

Both documents are generated from the endpoints themselves, so neither can name a route that does not exist. The consumer document filters the surface by the same four scope policies used to admit API client credentials, plus the public service-information route. Browser-only administration, setup, and session operations live only in the operator document, so a generated SDK cannot advertise methods no API key can call.

Routes, parameters, and request bodies normally come from signatures. Selective XML documentation supplies summaries and remarks where a path alone does not explain the operation; comments are written for useful semantics rather than to meet a coverage target. Documented handlers remain at least `internal`, because the compile-time XML processor omits private members, and each `AddOpenApi` document name remains a literal expression so the processor can intercept its registration. Explicit endpoint names provide stable, unique `operationId` values for generated method names.

Some HTTP facts are read deliberately through `HttpContext` and therefore need explicit OpenAPI metadata: upload is a required multipart object with one binary `file` field; Parse Run creation exposes the optional `Idempotency-Key` header; conditional preview and byte-range downloads declare their headers and `304`, `206`, and `416` responses; query pagination records defaults and accepted bounds. Provider options remain extensible JSON properties but are constrained to an object rather than an unconstrained value.

What comes back does not come from the signature: these handlers return `IResult`, which describes nothing, so response shapes and status codes are exactly the `Produces` metadata an endpoint declares plus cross-cutting responses supplied by transformers. Scope-gated operations declare `401` and `403`. An endpoint that declares no success is described as answering nothing at all, and that reads to an integrator as the contract rather than as an omission, so every operation declares its own.

A parameter with a fixed set of legal values says so too. `{format}` on the export route is a `string` in the signature and one of four values in practice, and it is listed from the same array the handler validates against, so a format the service accepts cannot be one the document leaves out.

Two things are not visible in an endpoint signature and are supplied by transformers in `src/StructaDoc.Host/OpenApi`:

- **Who may call it.** Every operation gated by a scope policy carries the `ApiKey` security requirement and says which scope it needs, and every operation admitted by a permission on a Document says which permission. Every operation reachable only from a browser says so instead, and offers no credential, because none would work. An endpoint that opts out of its group's policy is described as open, since describing the group's policy would state a requirement nothing enforces.
- **What the group is.** Operations are tagged Documents, Parse Runs, Administration, Sessions, or System. Ungrouped, every operation lands under the assembly name and a reader is handed one undifferentiated list.

The scope is stated in prose rather than carried in the security requirement because OpenAPI attaches scopes to OAuth flows alone, and an API client credential is not one. A signed-in browser session reaches the same endpoints holding no scope at all.

## The Permission Behind a Route

A scope opens the endpoint; a permission on the Document decides whether this caller reaches this resource. The two are not the same requirement and a route can want more of the second than the first suggests: `GET /api/v1/parse-runs/{parseRunId}/exports/{format}` is gated by `parses:read` and refuses a caller who holds read access without `Export`.

That second requirement used to be invisible. A scope policy is endpoint metadata, so the description could read it, while a permission was a question asked of the database partway through a request, and nothing outside the code that asked it could see it. The description named the scope, stopped, and invited calls it could not have known would fail.

Routes now declare it with `RequiresDocumentPermission(...)` beside `RequireAuthorization(...)`, and `ApiSecurityTransformer` states it. Where the check sits in the handler, the handler reads the requirement back out of that metadata rather than naming a permission a second time, so the promise and the enforcement cannot drift apart. Where it sits behind the service boundary — access grants, deletion, the read services' own filtering — the declaration reports what that layer requires.

The refusal is `404`, not `403`, so that a caller cannot learn a Document exists by being turned away from it. The description says so, because that is the part a reader would otherwise misdiagnose as a wrong identifier.

Health probes are excluded. They are endpoints, but they are an operational contract rather than the API, and describing them would invite an integration to depend on their shape.

## The Credential

The credential is described as an API key in the `Authorization` header rather than as an HTTP authorization scheme, because the header value carries its own `ApiKey ` prefix. Described the other way, a generated example would omit the prefix and fail to authenticate.

A scope authorizes the endpoint; ownership or an explicit grant authorizes the resource. See [Authentication](./authentication.md#resource-boundary) and [ADR-0008](../adr/0008-api-client-resource-isolation.md). The document says so in its overview, because a reader who sees only the scope list will otherwise expect a key to reach the whole workspace.

Browser-only endpoints are described in the separate operator document and are not offered an API-client credential. Keeping the operator surface visible without putting it in the consumer contract lets administrators inspect the complete Host while integrations generate only callable methods.

## Reachable Without Signing In

Neither route requires a credential. The web application is public static content that already contains every route in the document, so a credential here would withhold nothing from anyone who wanted it, while costing an integrator the one page they need before they have a key. What the endpoints *do* is authorized on every request, unchanged.

## Dependencies

The document is produced by `Microsoft.AspNetCore.OpenApi`, which is the platform's own support and needs nothing else.

The page is `Swashbuckle.AspNetCore.SwaggerUI`, and only that package: none of Swashbuckle's own document generation is used. It carries its assets as embedded resources, which is why it is here rather than a script tag pointing at a CDN. A deployment on an isolated network is the deployment this product is built for, and a page that quietly needed the internet would be blank in exactly those installations and nowhere else. `web/e2e/api-description.spec.ts` holds that against the published image by failing if the page requests anything off-host.

## Verification

`tests/StructaDoc.Host.Tests/ApiDescriptionTests.cs` covers the parts a generator cannot infer: the consumer/operator split, stable unique operation IDs, the multipart and header contracts, pagination bounds, cache and range responses, the credential form, scopes, permissions, grouping, result DTOs, and the self-hosted browsing page.

One of those tests holds that every route named in the document's overview is a route the document describes. The overview is prose, and prose is where a route goes stale without anything failing.

The CI workflow then exports `/api/v1/openapi.json`, generates a C# SDK with pinned OpenAPI Generator 7.24.0, and compiles the result. Structural tests and actual generation cover different failure modes, so both gate publishing.
