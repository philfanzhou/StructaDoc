# ADR-0007: Authorized resources and durable cleanup

- Status: Accepted
- Date: 2026-08-07

## Decision

Documents have an optional external owner identified by `(issuer, subject)` and
may have explicit access grants. Resources created by legacy administrators or
API clients remain valid and are governed by administrator/service policies.

Document and Parse Run deletion is a durable state transition, not a request
that deletes database rows before external storage cleanup. A deletion request
marks the target unavailable, snapshots every referenced storage object into a
persistent cleanup job, and returns an accepted result. A worker performs
idempotent object deletion and only then removes relational rows in a database
transaction. Transient failures are retried and remain observable.

Raw provider results remain downloadable artifacts behind explicit permission.
They are never merged into the versioned canonical Block contract.

## Consequences

- Failed object deletion cannot silently produce an apparently completed
  deletion.
- Resource reads exclude deletion-pending targets.
- Active Parse Runs must reach a final state before their Document or result can
  be deleted.
- Storage providers must implement idempotent delete and conflict-safe write.
