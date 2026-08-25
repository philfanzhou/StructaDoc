# ADR-0009: Persist Actor Identities as Structured Pairs

- Status: Accepted
- Date: 2026-08-25

## Context

StructaDoc currently records the authenticated actor in `documents.created_by` and
`parse_runs.created_by` as one string. The two write paths do not produce the same
string:

- Document upload writes `{subject-type}:{subject-id}`. For an OIDC user this omits
  the issuer, even though the stable identity is `(issuer, subject)`.
- Parse Run creation writes `oidc:{issuer}|{subject}` for an OIDC user and the
  subject-type form for administrators and API clients.

Both columns are limited to 255 characters. The authentication boundary accepts an
ASCII OIDC issuer up to 512 characters and an ASCII subject up to 255 characters, so
a valid identity can fail only when it reaches an audit write. In addition,
`parse_runs.created_by` is part of `ux_parse_runs_idempotency`; on MySQL and MariaDB
its default collation can compare opaque identifiers without ordinal, case-sensitive
semantics.

The persisted representation must cover OIDC users, local administrators, and API
clients without changing what the authentication boundary accepts. It must remain
portable across SQLite, PostgreSQL, MySQL, and MariaDB and must leave room for the
client-supplied Idempotency-Key in InnoDB's 3072-byte index-key limit.

## Decision

### Canonical representation

Persist an actor as the structured pair `(created_by_issuer, created_by_subject)`:

| Actor | `created_by_issuer` | `created_by_subject` |
|---|---|---|
| OIDC user | The validated OIDC issuer claim | The validated OIDC subject claim |
| API client | `structadoc:api-client` | The client ID as a lowercase `D`-format UUID |
| Local administrator | `structadoc:administrator` | The administrator ID as a lowercase `D`-format UUID |

`created_by_issuer` accepts at most 512 ASCII characters and
`created_by_subject` accepts at most 255 ASCII characters. The pair is either wholly
present or wholly absent. New authenticated writes must always supply both values;
an absent pair is reserved for historical or system-authored data for which no actor
was recorded.

OIDC values are persisted exactly as accepted by `ExternalIdentityConstraints`.
StructaDoc does not trim, case-fold, URI-normalize, percent-decode, or apply Unicode
normalization at the persistence boundary. UUID-backed subjects are parsed as UUIDs
and emitted with `Guid.ToString("D")`, which supplies the one canonical lowercase
text form for those application-owned identities.

`structadoc:api-client` remains the reserved issuer from ADR-0008.
`structadoc:administrator` is a second reserved issuer. Neither can collide with an
OIDC issuer because the authentication boundary accepts only absolute `http` and
`https` issuer URIs.

Two canonical actors are equal only when both fields are equal by ordinal,
case-sensitive comparison. The representation introduces no probabilistic
collision: unequal accepted pairs remain unequal persisted pairs. Database mappings
must enforce the same comparison:

- SQLite uses `BINARY` collation;
- PostgreSQL uses the built-in `C` collation;
- MySQL and MariaDB use the `ascii` character set with `ascii_bin` collation.

The client-supplied `idempotency_key` remains Unicode-capable and also compares
ordinally: `BINARY` on SQLite, `C` on PostgreSQL, and `utf8mb4_bin` on MySQL and
MariaDB. It must not be changed to `ascii_bin` because its accepted input is not
restricted to ASCII.

Operators can read a new actor directly from the two columns. API-client and
administrator subjects resolve through their respective control-plane records.
An OIDC pair resolves through the configured identity provider; StructaDoc does not
create a shadow user directory merely for audit display.

### InnoDB index budget

`ux_parse_runs_idempotency` will contain the two actor columns, `document_id`, and
`idempotency_key`. The conservative maximum indexed data length on the current
MySQL and MariaDB mapping is:

| Indexed value | Encoding and maximum | Bytes |
|---|---:|---:|
| `created_by_issuer` | 512 ASCII characters | 512 |
| `created_by_subject` | 255 ASCII characters | 255 |
| `document_id` | `char(36)` ASCII UUID | 36 |
| `idempotency_key` | 256 `utf8mb4` characters, 4 bytes each | 1024 |
| **Total** | | **1827** |

The total is 1245 bytes below InnoDB's 3072-byte limit. It uses the widest current
UUID mapping rather than assuming a future 16-byte binary UUID. Therefore every
identity accepted by `ExternalIdentityConstraints` fits the index on MySQL and
MariaDB. SQLite and PostgreSQL do not impose the InnoDB key limit, and use the same
declared character maxima and comparison semantics.

### Existing rows and compatibility

Existing actor strings must not be parsed into canonical pairs. The Document form
lost the OIDC issuer, and the Parse Run form used delimiters that were not excluded
from accepted issuer and subject values. Inferring field boundaries would silently
invent identity facts.

During each affected migration, every non-null legacy `created_by` value is instead
copied byte-for-byte into this compatibility pair:

- `created_by_issuer = structadoc:legacy-actor-v1`
- `created_by_subject = <the former created_by value>`

The old column already limits these application-produced, ASCII values to 255
characters, so they fit the subject column. The migration must validate that fact
before removing the old column and fail with an actionable error if manually inserted
data violates it. Null legacy actors remain an absent pair. The reserved legacy
issuer cannot collide with a new OIDC, API-client, or administrator identity.

This preserves the exact historical audit text for operators without pretending it
contains information the old writer never stored. Legacy pairs are compatibility
records, not a fourth kind of identity accepted for new writes.

Idempotency-Key replay recorded before the upgrade remains addressable. For a request
that has an Idempotency-Key, Parse Run creation checks both:

1. the request's canonical structured pair; and
2. `("structadoc:legacy-actor-v1", <exact actor string produced by the old Parse Run writer>)`.

An existing legacy match is replayed; a newly created run stores only the canonical
pair. This intentionally preserves the old writer's comparison domain for already
recorded operations. If two formerly accepted OIDC pairs produced the same delimited
legacy string, the old row cannot be disambiguated after the fact and retains its
pre-upgrade replay behavior. New rows cannot have that collision because their two
fields are compared separately.

### Migration ownership and ordering

Issue #35 requires schema migrations for all four databases. Its migration adds the
two Document actor columns, backfills legacy pairs, validates pair consistency, and
removes `documents.created_by`. Document ingestion then writes only canonical pairs.

Issue #36 also requires schema migrations for all four databases and must deliver
issue #26 in the same migration series. That series performs the following order:

1. add the two Parse Run actor columns with the ordinal mappings above;
2. backfill and validate the reserved legacy pairs;
3. drop `ux_parse_runs_idempotency` once;
4. remove `parse_runs.created_by` and apply the ordinal `idempotency_key` mapping;
5. create `ux_parse_runs_idempotency` once over
   `(created_by_issuer, created_by_subject, document_id, idempotency_key)`; and
6. switch creation and replay lookup to canonical-plus-legacy pair lookup.

Thus #35 has its own Document schema change, #36 has a Parse Run schema change, and
#26 is implemented by #36's one index/collation migration rather than by a second
index rebuild.

## Alternatives Considered

### Fixed-width derived key

The evaluated form was a 32-byte SHA-256 digest of a version byte, a four-byte
big-endian issuer length, the exact issuer ASCII bytes, a four-byte big-endian
subject length, and the exact subject ASCII bytes. It would use ordinal binary
comparison and reduce the current InnoDB index budget to `32 + 36 + 1024 = 1092`
bytes. Its collision semantics would be probabilistic, with a second-preimage chance
bounded by SHA-256 rather than impossible by representation.

The digest is not reversible. Resolving it would require a new identity-mapping table
or recomputing candidates from control-plane and identity-provider records; the latter
cannot resolve a deleted external user. That cost and loss of audit readability are
unnecessary because the structured pair already fits the index with ample headroom.

### Readable prefix plus digest

The evaluated form used the same length-prefixed digest input as the fixed-width
option. Its persisted ASCII value would be `v1:`, followed by the first 96 bytes of
the exact `issuer + "|" + subject` bytes, followed by `:`, followed by the 64
lowercase hexadecimal SHA-256 characters. No trimming, case-folding, URI
normalization, percent-decoding, or Unicode normalization would occur; UUID subjects
would first use their lowercase `D` form. The maximum would be 164 ASCII bytes and
equality would be ordinal. Identity equality would still depend on the digest, so its
collision semantics would be the same probabilistic SHA-256 semantics as the fixed
key.

The prefix improves diagnosis for short identities but is ambiguous when either part
contains `|`, and truncation prevents reliable recovery of long identities. An
operator still needs the mapping mechanism required by the fixed-width option, while
the representation is larger and more complicated.

### Structured pair without legacy compatibility

Parsing the current strings and backfilling them as if they were canonical would make
the final schema superficially simpler. It is rejected because the Document string
does not contain an OIDC issuer and the Parse Run delimiters are not an injective
encoding. Dropping old replay lookup is also rejected because a client retrying an
operation across an upgrade must receive the already-created Parse Run rather than
enqueue duplicate Provider work.

## Consequences

### Positive

- Every accepted actor identity fits both audit tables and the Parse Run idempotency
  index on every supported database.
- Audit rows remain directly readable and exact identity equality is not reduced to a
  hash-collision assumption.
- OIDC issuer and subject are no longer flattened into an ambiguous string.
- Legacy audit text and Idempotency-Key replay remain available without inventing
  missing identity information.
- #26 and #36 have one explicit index rebuild and one collation contract.

### Trade-offs

- Documents and Parse Runs each require two replacement columns and migrations on all
  four database providers.
- Parse Run replay temporarily needs a second lookup representation for rows created
  before the migration. That compatibility path cannot be removed while such rows
  are retained.
- Historical Document rows whose old actor omitted an OIDC issuer remain historical
  strings; the migration cannot recover an issuer that was never stored.
