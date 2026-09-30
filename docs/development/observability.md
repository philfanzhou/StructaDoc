# Observability

- Status: Implementation note
- Last updated: 2026-09-30

## Service Identity

The Host registers a service identity through ServiceMantle (`AddServiceMantle`) before anything
else is built:

- `ServiceId` is the stable value `structadoc`. It is the deployment identity shared by every
  instance of this service, not a per-run value.
- `InstanceId` is `structadoc-<random>` and is regenerated on every host start. It identifies one
  running process, not a persistent deployment, and two restarts are not the same instance.
- The service version is resolved by the library from the entry assembly informational version,
  then the assembly version, then `unknown`. The Host passes no explicit version. Release builds
  stamp the informational version from the release tag and commit (see `SOURCE_REVISION` in the
  `Dockerfile`), which is what makes two builds distinguishable.

The identity is exposed as a singleton `ServiceLogContext` and `/api/v1/system/info` answers its
`Version` field from `ServiceLogContext.ServiceVersion`, so the HTTP answer and the log identity
cannot disagree. The response contract is unchanged: `ServiceInfoResponse(Name, Version)` with
`Name` fixed to `StructaDoc`.

## Structured Logging Pipeline

Console logging runs through the ServiceMantle Serilog pipeline (`AddServiceMantleSerilog`),
registered before the host is built so startup logging — including the stored-configuration fault
warnings — flows through it:

- Minimum level `Information`, with per-category overrides raising `Microsoft.AspNetCore` and
  `Microsoft.EntityFrameworkCore.Database.Command` to `Warning`, matching the shipped
  `appsettings.json` values. Events below the override level in those categories are not emitted.
- Scopes are propagated, so a `ServiceLogContext.BeginScope(logger)` scope emits the identity
  fields `ServiceName`, `ServiceVersion`, and `InstanceId` as structured properties on every event
  written inside it.
- Structured properties are sanitized by the library before they reach the sink; sanitization
  cannot be disabled through options. Secret-shaped values are rejected or masked rather than
  written to the console.
- Registering the pipeline removes the default Microsoft.Extensions.Logging console providers, so
  no event can bypass the sanitizing boundary. A conflicting pre-existing Serilog configuration
  fails host startup with the stable error code `serilog.console_sink_conflict`; equivalent
  repeated registrations are idempotent.

The `Logging:LogLevel` section in `appsettings*.json` remains a Microsoft.Extensions.Logging
pre-filter in front of the pipeline: it can suppress events further, but it cannot raise events
above the pipeline minimum level or its per-category overrides. Both layers are in force at once.

The `--migrate-business-database` CLI path does not use this pipeline. It exits before the web
host is built and keeps its own `Microsoft.EntityFrameworkCore` suppression, so migration command
log output and behavior are unchanged.

## Request Correlation

The Host resolves exactly one Correlation ID per request through the ServiceMantle correlation
middleware (`UseServiceMantleCorrelationId`), inserted after the reverse-proxy trust and before
every other downstream component, so its coverage is the complete downstream response surface,
including rate-limited 429s, API 401/404s, static files, and the SPA fallback:

- Every response answers with that value in the `x-correlation-id` response header. This is an
  additive change to the public HTTP surface within `v1`: no existing header or field changed.
- The same value is published to the request context (`TryGetServiceMantleCorrelationId`) and to
  the downstream `ILogger` scope, where it appears as the structured field `CorrelationId` beside
  the identity fields `ServiceName`, `ServiceVersion`, and `InstanceId`. One `HttpContext.Items`
  slot and one `Response.OnStarting` callback keep the three surfaces in agreement.
- A caller-supplied header value is reused verbatim only when the request carries exactly one
  value of 1–64 characters whose first character is an ASCII letter or digit and whose remaining
  characters are ASCII letters, digits, `.`, `_`, or `-`. Missing, empty, whitespace, overlong,
  illegal, comma-joined, and repeated headers are discarded as a whole and replaced by a generated
  value matching `^[0-9a-f]{32}$`. The original request header is never rewritten, and the
  rejected raw value never reaches the response header, the log scope, or a diagnostic object.
- The scope wraps the whole downstream call and is released on success, failure, and cancellation
  alike; downstream exceptions and cancellation propagate unchanged.

A Correlation ID is a log-correlation handle, nothing more. It is not unique, unguessable, or
unforgeable; it must not be used for authorization, idempotency, replay protection, or audit
subject identity. It is not propagated to outbound `HttpClient` calls (MinerU, S3, OIDC) and is
not mapped to W3C `traceparent` or OpenTelemetry. Responses produced outside the middleware —
transport-level errors, aborted connections, responses already started before it ran — are not
guaranteed to carry the header. Consumers should treat it as the key for matching a report to a
request in the service logs.

## Explicit Non-Guarantees

- Free text an application interpolates into a message template is not sanitized by the library;
  callers remain responsible for not putting secrets into message templates.
- The pipeline cannot flush events when the process is killed without notice (for example
  `SIGKILL`); events still buffered may be lost.
- There is no remote log transport. Console stdout is the only sink, and forwarding it to
  anything else is a deployment-side responsibility.
