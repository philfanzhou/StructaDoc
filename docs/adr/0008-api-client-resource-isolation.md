# ADR-0008: Bound API Clients by Ownership Rather Than Trust Them Globally

- Status: Accepted
- Date: 2026-08-19
- Supersedes in part: [ADR-0005](./0005-authentication-and-api-clients.md), [ADR-0006](./0006-user-workspace-and-oidc.md), [ADR-0007](./0007-resource-lifecycle-and-cleanup.md)
- Superseded in part by:
  [ADR-0009](./0009-canonical-persisted-actor-identity.md)

## Context

[ADR-0005](./0005-authentication-and-api-clients.md) gave machine callers their own credential and a set of scopes. [ADR-0006](./0006-user-workspace-and-oidc.md) then described them as "trusted service principals within those scopes", and [ADR-0007](./0007-resource-lifecycle-and-cleanup.md) left resources they create governed by service policy rather than by an owner. The implementation followed: every resource filter admitted an API client on the same branch as an administrator.

A scope therefore constrained which verbs a key could use and nothing about which resources it could use them on. Three consequences followed from that, and none of them was intended:

- One leaked key exposed every Document in the deployment, including those uploaded through the browser by people who never heard of that integration.
- Two integrations could not share a deployment. Each could read, re-parse, export, and delete the other's Documents, so isolating them meant running two deployments.
- A client could not be given less than everything. `documents:read` was the smallest readable unit and it meant the whole workspace.

The workspace already has a mechanism for exactly this question. Documents carry an owner keyed by `(issuer, subject)` and may carry explicit grants, and OIDC users are bounded by it. API clients were outside that model only because they predate it.

## Decision

An API client is a principal in the workspace, not a service with administrative reach and narrower verbs.

- API clients and OIDC users share one key space, `(issuer, subject)`. A client's issuer is the reserved value `structadoc:api-client` and its subject is the client ID.
- The reserved issuer cannot collide with a real one. A valid OIDC issuer is an absolute `http`/`https` URI, which `structadoc:` is not, so no identity provider can present it and no OIDC subject can be mistaken for a client ID.
- A Document uploaded by an API client records that client as its owner. An owner holds every document permission.
- One owner-or-grant filter serves every non-administrator principal. The branch that admitted API clients unconditionally is removed rather than narrowed.
- An access grant may name an API client, so a Document may be shared with an integration on the same terms it is shared with a person, and by its owner rather than only by an administrator.
- Administrators are unchanged and still reach every resource. A Document uploaded by an administrator stays unowned, because an administrator is not a workspace principal and needs no ownership to reach it.
- Scopes are unchanged and still gate endpoints. A scope now answers which verbs, and ownership answers which resources; neither substitutes for the other.

Existing Documents were attributed rather than orphaned. At the time this decision
was implemented, the scalar `created_by` column recorded which API client uploaded a
Document, so the migration recovered ownership from it in all four supported
databases. ADR-0009 later replaces that audit representation with a canonical actor
pair or legacy compatibility payload and adds `structadoc:administrator` as another
reserved persisted-actor issuer. It does not change the owner attribution already
performed by this decision.

## Consequences

### Positive

- A leaked key exposes what that client uploaded or was granted, not the deployment.
- Independent integrations share a deployment without seeing each other's Documents.
- Authorization has one rule for everyone who is not an administrator, so a filter that is correct for OIDC users cannot be quietly wrong for machines.
- Sharing with an integration is an ordinary grant, visible and revocable next to every other grant on the Document.

### Trade-offs

- This is a breaking change for existing API clients. A client that read Documents it did not upload stops being able to, and no scope restores that.
- Access is granted one Document at a time. An OIDC user whose Documents an integration must parse has to grant each one; there is no principal-level grant meaning "everything I own, now and later".
- A genuinely workspace-wide integration, such as an external backup, has no supported credential. It would need per-Document grants, which does not scale, or an administrator session, which ADR-0005 keeps away from machines.
- Ownership is now load-bearing for a subject type that previously ignored it, so a future subject type must decide its ownership story before it can call these endpoints.

## Rejected Alternatives

- **A per-client switch between workspace-wide and isolated access.** Keeps the unsafe default reachable and makes what a key can see depend on a setting that is not in the key, so reading the credential no longer tells you its reach. It also leaves both code paths to be maintained and tested.
- **A separate owner column for API clients.** Requires a schema migration in four databases and splits one key space into two, which every authorization query would then have to handle twice.
- **Isolation without the backfill migration.** Documents an API client uploaded would silently become unreachable to it on upgrade, which is indistinguishable from data loss for whoever is holding the key.
- **Principal-level grants in this change.** Worth having, but it is a new sharing concept with its own revocation and listing semantics, and bundling it would mean shipping the security fix behind a feature.
