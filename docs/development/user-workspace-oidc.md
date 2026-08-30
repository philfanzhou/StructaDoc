# User Workspace and OIDC

StructaDoc's web interface is a user-facing product, not only an administrator console. Signed-in users can upload and filter their own documents, create Parse Runs, inspect normalized results, download originals, export results, and share document permissions. Administrators additionally configure Providers and manage API clients.

## Route Layout

One Host serves both audiences and the API on a single address, as required by [ADR-0003](../adr/0003-technology-and-single-image-deployment.md):

```text
/               the document workspace
/setup          first-run administrator creation, only while none exists
/signin         workspace sign-in
/admin          the administration area
/admin/signin   administrator sign-in, including local break-glass access
/api/v1/...     the service API
/api/v1/docs    the browsable API description
```

A path prefix is not an access boundary. The web application is public static content that any visitor can download, so `/admin` protects nothing by itself; every administrative route is enforced by the administrator policy on the server. The split exists so the two audiences get their own entry point and so the administration bundle is a lazily loaded chunk that a workspace-only visitor never downloads.

Until the first administrator exists, every client route leads to `/setup`; afterwards `/setup` redirects away and its endpoint returns `404`. See [Authentication](./authentication.md) for the claim window and the administrator-only warning that compensates for it.

Local administrator credentials are entered only at `/admin/signin`. The workspace sign-in page offers OIDC alone, and points administrators at the administration entry point when OIDC is disabled.

Because the workspace and administration areas are client-side routes, the Host returns the application shell for unmatched navigation paths. `/api` and `/health` paths are excluded from that fallback so a mistyped route fails as an API call instead of answering `200` with HTML.

## Identity Boundary

- External interactive users sign in with standard OIDC Authorization Code flow and PKCE.
- The stable identity key is `(issuer, subject)`, not email, username, or a Provider-private user ID.
- `issuer` is an ASCII HTTP(S) OIDC issuer without query or fragment, up to 512 characters. `subject` follows the 255-character ASCII bound used by the implementation.
- Both identity parts compare case-sensitively. MySQL and MariaDB use `ascii_bin` so a default collation cannot merge distinct subjects.
- Authority, client, scopes, and claim/role mapping come from the generic `Oidc` configuration section, which an administrator can also fill in from the browser.
- SignaCore can act as a compatible OIDC Provider, but StructaDoc does not reference or bind to SignaCore code or private contracts.
- The local administrator is a username-based account in a separate local control-plane database, kept for first-run setup and break-glass access during identity-Provider outages.
- API clients retain independent keys and scopes, never reuse browser cookies, and are principals in the same `(issuer, subject)` key space under the reserved issuer `structadoc:api-client`. See [ADR-0008](../adr/0008-api-client-resource-isolation.md).

The Host handles OIDC tokens and creates an encrypted HttpOnly application session after callback. Browser JavaScript does not receive the tokens.

## Ownership and Sharing

A document records the principal that created it, whether that was an OIDC user or an API client. Owners have full document permission. Explicit grants target another `(issuer, subject)`, which may be either kind of principal, and contain a subset of:

- `read`
- `parse`
- `export`
- `delete`
- `share`

Every document, Parse Run, Page, Block, Asset, Artifact, Markdown view, export, share operation, and deletion performs resource-level authorization. One owner-or-grant rule covers every principal, so an API client sees what it uploaded and what it was granted rather than the whole workspace. Administrators are the only global policy; a document an administrator uploads is unowned and stays administrator-only.

## Reading a Result

Whether a parse is any good is a question about the result, and it is the question the workspace exists to answer. A Parse Run's result is shown as four tabs over one selection rather than one column, because someone is either reading the document or checking its structure, and those want different things on screen:

- **Document** is the Markdown rendered by `markdown/preview`, in an iframe with the `sandbox` attribute. The content came out of a Provider archive, so it is never given the workspace's own origin to run in; the response carries `Content-Security-Policy: sandbox` as well. A run with no Markdown Artifact has nothing here, and the panel opens on the structure instead.
- **Structure** lists Blocks in reading order with their type, page, confidence, and referenced Asset. Blocks arrive one cursor page at a time and are appended on request. The count on screen is what has been loaded, never a total: there is no count endpoint, and a number the page cannot know is worse than no number.
- **Layout** draws one page's Blocks as boxes on the page's own shape, numbered in reading order and coloured by type, and clicking a box shows that Block. Bounding boxes are normalized to the page, so this needs no page raster and no dimensions to be in the right relative places; Provider dimensions give the shape, and without them the boxes are drawn on A4 and the panel says so. It reads one page at a time through the Blocks endpoint's `pageNumber` filter rather than paging the whole run. Blocks with no bounding box are counted and named as absent rather than dropped.
- **Resources** shows image Assets as thumbnails and every Artifact by type, both linked at their authorized content routes.

Block types come from the canonical model, whose registered set is allowed to grow inside one API major version. An unrecognized type is coloured and displayed under its own name rather than dropped, which is what the model asks consumers to do.

Every result read is checked against the current selection when it returns. Selecting a second Parse Run while the first one's reads are in flight is one click, and without the check the slower answer lands in the newer run's panel.

## Watching Work in Progress

Parsing is asynchronous and finishes on the service without telling the browser, so the workspace re-reads what is unfinished on a three-second timer. The timer is driven by the visible state rather than left running: it starts when a Parse Run or a document in the list holds a non-final status and stops when everything in view is `succeeded`, `failed`, or `cancelled`, so a workspace showing only finished work makes no requests at all. Each pass schedules the next one only after it finishes, including after a transient failure, so slow requests cannot overlap or accumulate. Unmounting the workspace cancels a pending timer and prevents another pass from being scheduled. The workspace says while it is polling, so a screen that is not changing is distinguishable from one that has stopped watching.

A background pass captures the selected Document and Parse Run when it starts, applies the Run list only while that Document remains selected, and stays silent when it fails, because a service that is briefly unreachable should not clear the screen or interrupt someone reading a result. Opening another Document clears the previous Run list immediately; success or failure from the previous selection is then ignored. A refresh the user asked for still reports its failure. Pages, Blocks, Assets, and the Markdown view only exist once a run reaches a final status, so they are read at that transition rather than on every pass.

This is polling rather than a push channel. It costs one list request per interval per open workspace, against a stream that would have to survive a reverse proxy, a restart, and a lost connection to be worth the exchange at this scale.

## Configuration

Sign-in through an identity provider is configured under `/admin`, because it is the only way an end user reaches the workspace: a deployment that could not configure it from the browser would have no users at all. The keys are ordinary settings and follow the precedence in [Service Settings](./service-settings.md), so a deployment that prefers to pin them keeps doing that and the web interface reports them as managed externally.

```json
{
  "Oidc": {
    "Enabled": true,
    "Authority": "https://identity.example.com",
    "ClientId": "structadoc",
    "ClientSecret": "from-secret-store",
    "RequireHttpsMetadata": true,
    "Scopes": ["openid", "profile", "email"],
    "RoleClaim": "role",
    "AdministratorRole": "structadoc-admin"
  }
}
```

`Oidc:Scopes` is an array, which the settings store cannot express as one key and one value, so it is not settable from the browser. The administration page reports the scopes in force so an administrator can see what is requested without having to find out by reading a sign-in request. `CallbackPath` and `SignedOutCallbackPath` are reported for the same reason: they have to be registered at the identity provider, and the page composes them against the address the browser actually reached, which is the only place that knows what a reverse proxy publishes.

Behind a proxy that terminates TLS, the address the service composes is that reported address only once the proxy is trusted. Until then it says `http` and carries the internal host, and the sign-in request is refused by the identity provider for a redirect address that does not match what was registered. A deployment with an identity provider behind a proxy therefore needs both `ReverseProxy:TrustedProxies` and `ReverseProxy:PublicHosts`; see [Behind a Reverse Proxy](../deployment/single-container.md#behind-a-reverse-proxy).

A client secret set from the browser is encrypted with the Data Protection key ring and never sent back; only whether one is set is reported. Injecting it through a deployment secret instead remains supported and takes precedence. HTTPS metadata should remain required outside explicitly isolated development environments.

### Getting It Wrong

Settings are written one key at a time, so enabling sign-in before filling in the authority is an ordinary order to do it in, and the combination is what fails rather than any single write. If the service refused to start on that, a deployment whose only administration surface is the browser would have nothing left to fix it from. A stored `Oidc` section that fails validation is therefore dropped at startup, the service starts with sign-in disabled, and the administration page reports what was rejected. Local administrator sign-in does not depend on the identity provider, so the way back in stays open. A value the deployment pins still stops the service, because that operator has a command line and is better served by failing immediately.

`POST /api/v1/admin/settings/oidc/test` fetches the authority's discovery document and checks that it parses, carries the endpoints a sign-in needs, and reports the same issuer as the address it was fetched from. The issuer check is the one that matters in practice: the middleware rejects tokens whose issuer is not the configured authority, so a mismatch would otherwise surface only when a real user tried to sign in. The test says nothing about the client id and secret, which cannot be verified without completing a sign-in.

Unlike Provider transfers, this request is allowed to reach private addresses. A self-hosted deployment's identity provider is very often on the same private network, so refusing those would reject the common case.
