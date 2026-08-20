# API Description

StructaDoc is integrated with by other systems. Until now the only description of its API was prose in this repository: it could not be handed to a code generator, could not be diffed when an endpoint changed, and was not open in front of anyone writing a request. The service now publishes an OpenAPI 3.1 document and a page that browses it.

## Where It Is

| Route | What it serves |
|---|---|
| `GET /api/v1/openapi.json` | the OpenAPI 3.1 document |
| `/api/v1/docs` | a browsable page rendering that document |

Both live under `/api` so the description travels with the API it describes, including through a reverse proxy that publishes only that prefix, and so the Host's client-route fallback already excludes them from the application shell.

`v1` in the path is the contract version, not the build. A build that adds an optional field does not change what a client is coding against; `GET /api/v1/system/info` reports which build is answering.

## What It Describes

The document is generated from the endpoints themselves, so it cannot name a route that does not exist or miss one that does. Routes, parameters, and request bodies come from the signatures. What comes back does not: these handlers return `IResult`, which describes nothing, so response shapes and status codes are exactly the `Produces` metadata an endpoint declares. An endpoint that declares none is described as answering nothing at all, and that reads to an integrator as the contract rather than as an omission, so every operation declares its own.

Two things are not visible in an endpoint signature and are supplied by transformers in `src/StructaDoc.Host/OpenApi`:

- **Who may call it.** Every operation gated by a scope policy carries the `ApiKey` security requirement and says which scope it needs. Every operation reachable only from a browser says so instead, and offers no credential, because none would work. An endpoint that opts out of its group's policy is described as open, since describing the group's policy would state a requirement nothing enforces.
- **What the group is.** Operations are tagged Documents, Parse Runs, Administration, Sessions, or System. Ungrouped, every operation lands under the assembly name and a reader is handed one undifferentiated list.

The scope is stated in prose rather than carried in the security requirement because OpenAPI attaches scopes to OAuth flows alone, and an API client credential is not one. A signed-in browser session reaches the same endpoints holding no scope at all.

Health probes are excluded. They are endpoints, but they are an operational contract rather than the API, and describing them would invite an integration to depend on their shape.

## The Credential

The credential is described as an API key in the `Authorization` header rather than as an HTTP authorization scheme, because the header value carries its own `ApiKey ` prefix. Described the other way, a generated example would omit the prefix and fail to authenticate.

A scope authorizes the endpoint; ownership or an explicit grant authorizes the resource. See [Authentication](./authentication.md#resource-boundary) and [ADR-0008](../adr/0008-api-client-resource-isolation.md). The document says so in its overview, because a reader who sees only the scope list will otherwise expect a key to reach the whole workspace.

Browser-only endpoints are described but not offered a credential. They are part of the surface, and omitting them would produce a document that looks complete and is not; marking them is what keeps an integration from building against a route no key can reach.

## Reachable Without Signing In

Neither route requires a credential. The web application is public static content that already contains every route in the document, so a credential here would withhold nothing from anyone who wanted it, while costing an integrator the one page they need before they have a key. What the endpoints *do* is authorized on every request, unchanged.

## Dependencies

The document is produced by `Microsoft.AspNetCore.OpenApi`, which is the platform's own support and needs nothing else.

The page is `Swashbuckle.AspNetCore.SwaggerUI`, and only that package: none of Swashbuckle's own document generation is used. It carries its assets as embedded resources, which is why it is here rather than a script tag pointing at a CDN. A deployment on an isolated network is the deployment this product is built for, and a page that quietly needed the internet would be blank in exactly those installations and nowhere else. `web/e2e/api-description.spec.ts` holds that against the published image by failing if the page requests anything off-host.

## Verification

`tests/StructaDoc.Host.Tests/ApiDescriptionTests.cs` covers the parts a generator cannot infer: that the document is served without a credential, that the security scheme is described in the form that actually authenticates, that scope-gated operations name their scope and offer the credential, that browser-only and anonymous operations do not, that every operation is grouped, that only the service API is described, and that the browsable page is served by the service itself.

One of those tests holds that every route named in the document's overview is a route the document describes. The overview is prose, and prose is where a route goes stale without anything failing.

Two more are invariants over the whole document rather than checks on one endpoint: that every operation says what a successful call returns, and that the Canonical Document Model types a client is generated against are in it. Both name what is missing when they fail, because what they guard against is an endpoint added later that quietly describes nothing.
