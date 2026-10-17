> Fill in the body in English. Use the English conventional commit format for the title
> (`feat:` / `fix:` / `docs:` …).

## Summary

Explain the problem being solved and what this change does.

Closes #

## Scope

Reference the "Scope" section of the linked issue and account for each item:

- **In scope, completed:**
- **Deliberately not fixed this time:** list them here as
  `#NNN — one-sentence description`; write "None" when confirmed absent.

## Contract and compatibility

- **Guaranteed behavior and non-guarantees:** whether they match the issue; write "None"
  when not applicable.
List the impact on the public API, the Canonical Model, the Parse Run state machine,
Providers, SQLite/PostgreSQL/MySQL/MariaDB, migrations, configuration, authentication,
security boundaries, or deployment. Write "None" when there is no impact.

## Verification

List the commands actually run and their results, and check the applicable items:

- [ ] Web dependency install and production build pass (`npm ci`, `npm run build`)
- [ ] .NET solution restore and build pass
- [ ] Automated tests that do not require Docker pass
- [ ] The relevant database contract tests pass
- [ ] Public API changes are verified through OpenAPI export and client generation
- [ ] When containers or user flows are involved, the production image and basic browser
      functionality are verified
- [ ] When behavior, contracts, or operational procedures change, the authoritative
      documentation is updated in sync
- [ ] No secrets, credentials, connection strings, or private document samples are
      included
