---
name: Task
about: A change that can be merged independently.
title: ''
labels: ''
assignees: ''
---

<!--
Write the body and title in English. Suggested title format: [module] concise action.
-->

## Background and goal

Describe the symptom, the trigger conditions, and the impact. Include reproduction
steps, failure output, and the affected databases or Providers when applicable.

Related code/docs: `src/...`, `web/...`, `docs/...`

## Behavior and contracts that must be preserved

- State explicitly what must remain unchanged in the public API, the Canonical Model,
  the Parse Run lifecycle, Providers, databases, storage, authentication, or deployment.
- If a fix must cover multiple call paths, state the requirement that every path must
  satisfy, and the common call site where a fix covers them all.

## Scope

- What will change.
- **What is explicitly excluded** — especially limits such as "no change to existing
  runtime behavior", "no modification of public contracts", "no refactoring of X", or
  "no new Provider implementation".

## Explicit exclusions and non-guarantees

Safety or robustness tasks must state guarantees, non-guarantees, and caller
responsibilities separately; other tasks write "None".

- Guarantees:
- Not guaranteed:
- Caller responsibilities:

## Acceptance criteria

- State results that can be verified one by one.
- Applicable failure, cancellation, security, authentication, resource, and concurrency
  outcomes; when one does not apply, state "not applicable" and why.
- When the public API, Providers, databases, storage, migrations, or deployment are
  involved, state the compatibility outcome.

## Documentation and compatibility impact

State whether the public API, the Canonical Model, the Parse Run lifecycle, the Provider
interface, database migrations, authentication and security boundaries, or the deployment
approach are affected. Write "None" when there is no impact.

## Other related problems (not fixed this time)

List them here:

- #NNN one-sentence description
- If confirmed absent, write "None".

## Dependencies

- GitHub native Blocked by: #NNN, or "None"
- GitHub native Blocks: #NNN, or "None"

## Delivery constraints

- When public behavior, contracts, deployment, or usage change, update the English
  README, docs, OpenAPI, and the related operational documentation in sync.
