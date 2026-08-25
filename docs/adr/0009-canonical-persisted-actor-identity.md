# ADR-0009: Persist Actor Identities as Structured Pairs

- Status: Accepted
- Date: 2026-08-25

## Context

StructaDoc currently records the authenticated actor in `documents.created_by`,
`parse_runs.created_by`, and `document_access_grants.created_by` as one string. The
write paths do not all produce the same string:

- Document upload writes `{subject-type}:{subject-id}`. For an OIDC user this omits
  the issuer, even though the stable identity is `(issuer, subject)`.
- Parse Run and access-grant creation write `oidc:{issuer}|{subject}` for an OIDC
  user and the subject-type form for administrators and API clients.

The Document writer enforces 255 .NET characters, while the Parse Run schema declares
255 characters and the access-grant schema declares 1024. SQLite does not enforce EF
Core maximum-length metadata, so it can already contain the full 773-character value
emitted by the Parse Run writer. The authentication boundary accepts an ASCII OIDC
issuer up to 512 characters and an ASCII subject up to 255 characters, including the
NUL byte in a nonblank subject. PostgreSQL text cannot represent NUL, and a valid
identity can therefore fail only when it reaches persistence. In addition,
`parse_runs.created_by` is part of `ux_parse_runs_idempotency`; on MySQL and MariaDB
its default collation can compare opaque identifiers without ordinal, case-sensitive
semantics.

The persisted representation must cover OIDC users, local administrators, and API
clients without changing what the authentication boundary accepts. It must remain
portable across SQLite, PostgreSQL, MySQL, and MariaDB and must leave room for the
client-supplied Idempotency-Key in InnoDB's index-key limit.

## Decision

### Canonical representation

Persist a new actor as the structured pair
`(created_by_issuer, created_by_subject)`:

| Actor | `created_by_issuer` | `created_by_subject` |
|---|---|---|
| OIDC user | ASCII bytes of the validated OIDC issuer claim | ASCII bytes of the validated OIDC subject claim |
| API client | ASCII bytes of `structadoc:api-client` | ASCII bytes of the client ID as a lowercase `D`-format UUID |
| Local administrator | ASCII bytes of `structadoc:administrator` | ASCII bytes of the administrator ID as a lowercase `D`-format UUID |

Each accepted ASCII code unit is stored as its one-byte value, including `0x00`.
`created_by_issuer` accepts at most 512 bytes and `created_by_subject` accepts at most
255 bytes. This encoding is one-to-one and reversible; it does not rely on a database
text type being able to represent every accepted subject.

Each affected audit record also has a nullable `created_by_legacy` binary field of at
most 1024 bytes. Exactly one of these states is valid:

- both canonical fields are present and `created_by_legacy` is absent;
- both canonical fields are absent and `created_by_legacy` is present; or
- all three fields are absent for historical or system-authored data for which no
  actor was recorded.

New authenticated writes always use the canonical pair. They never write the legacy
field.

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
case-sensitive byte comparison. The representation introduces no probabilistic
collision: unequal accepted pairs remain unequal persisted pairs. Canonical actor
fields, Document owner fields, and access-grant principal fields all use the same
binary mapping so the identity boundary does not change according to the field's
purpose:

- SQLite uses `BLOB`;
- PostgreSQL uses `bytea`;
- MySQL and MariaDB use `varbinary`.

These types compare the persisted bytes directly and can represent every accepted
subject, including NUL. They need no character set or collation.

The client-supplied `idempotency_key` remains limited to one to 256 visible ASCII
characters (`0x21` through `0x7e`) and also compares ordinally: `BINARY` on SQLite,
`C` on PostgreSQL, and the `ascii` character set with `ascii_bin` collation on MySQL
and MariaDB. This preserves the existing public API and persistence contracts; this
ADR does not broaden the accepted Idempotency-Key input.

Operators and application code decode each canonical byte as the same-valued ASCII
code unit. API-client and administrator subjects resolve through their respective
control-plane records. An OIDC pair resolves through the configured identity
provider; StructaDoc does not create a shadow user directory merely for audit
display.

`DocumentAccessGrantResponse.CreatedBy` remains a required string in `/api/v1`.
For a canonical row it is projected in the existing display form:
`oidc:{issuer}|{subject}`, `api-client:{subject}`, or
`administrator:{subject}`. For a legacy row it is the decoded former value. This
keeps the v1 DTO backward compatible while making only the persisted representation
canonical; the flattened response is not an identity key.

### InnoDB index budget

`ux_parse_runs_idempotency` will contain the two canonical actor columns,
`document_id`, and `idempotency_key`. The conservative maximum indexed data length
on the MySQL and MariaDB mapping is:

| Indexed value | Encoding and maximum | Bytes |
|---|---:|---:|
| `created_by_issuer` | 512 binary bytes | 512 |
| `created_by_subject` | 255 binary bytes | 255 |
| `document_id` | `char(36)` ASCII UUID | 36 |
| `idempotency_key` | 256 ASCII characters | 256 |
| **Total** | | **1059** |

The total is 2013 bytes below InnoDB's 3072-byte limit. That limit requires the
`DYNAMIC` row format and an InnoDB page size of at least 16 KiB; it is not an
unconditional InnoDB limit. These settings are part of StructaDoc's MySQL and MariaDB
support boundary. Before applying business-database migrations, the implementation
must read and validate `innodb_page_size`, and the Parse Run migration must request
`ROW_FORMAT=DYNAMIC` and verify the resulting table row format. An unsupported
configuration fails before the index is rebuilt with an actionable message.

The new 1059-byte index alone would fit the 1536-byte limit of an 8 KiB `DYNAMIC`
page. StructaDoc requires 16 KiB because a fresh installation must first apply the
existing migration whose `utf8mb4` actor and idempotency fields can budget 2080
indexed bytes before #36 replaces them. The supported minimum therefore remains the
3072-byte limit, leaving the stated 2013-byte headroom after #36.

The calculation uses the widest current UUID mapping rather than assuming a future
16-byte binary UUID. Therefore every identity accepted by
`ExternalIdentityConstraints` fits the index on supported MySQL and MariaDB
configurations. SQLite and PostgreSQL do not impose the InnoDB key limit, and use the
same declared byte maxima and comparison semantics.

Legacy payloads do not participate in the canonical unique index because they can be
up to 1024 bytes and no new legacy rows are written. Replay first narrows candidates
through a non-unique `(document_id, idempotency_key)` index, whose maximum InnoDB
data length is `36 + 256 = 292` bytes, and then compares the legacy payload exactly.

### Existing rows and compatibility

Existing actor strings must not be parsed into canonical pairs. The Document form
lost the OIDC issuer, and the Parse Run and access-grant form used delimiters that
were not excluded from accepted issuer and subject values. Inferring field boundaries
would silently invent identity facts.

During each affected migration, every non-null legacy `created_by` value is instead
encoded as strict UTF-8 without a byte-order mark and copied into
`created_by_legacy`. UTF-8 is reversible for the valid Unicode strings accepted from
OIDC and preserves ASCII control bytes such as NUL in a binary field. Null legacy
actors remain the all-absent state.

The compatibility field covers the actual old writer domains rather than trusting
unenforced EF Core length metadata:

- the Document writer can persist 255 .NET characters, including non-ASCII values;
  valid Unicode of that length needs at most 765 UTF-8 bytes;
- the Parse Run and access-grant writers can emit
  `oidc:` + 512 issuer bytes + `|` + 255 subject bytes, or 773 UTF-8 bytes; and
- administrator and API-client forms are shorter.

The 1024-byte compatibility field therefore preserves every application-produced old
value. A migration fails with an actionable error only for an invalid Unicode value
or a value beyond these old writer domains, which indicates manual or out-of-band
data rather than treating a valid deployment as corrupt.

This preserves the exact historical audit text for operators without pretending it
contains information the old writer never stored. Legacy payloads are compatibility
records, not a fourth kind of identity accepted for new writes.

Idempotency-Key replay recorded before the upgrade remains addressable. For a request
that has an Idempotency-Key, Parse Run creation checks both:

1. the request's canonical structured pair; and
2. the UTF-8 legacy payload for the exact actor string produced by the old Parse Run
   writer.

An existing legacy match is replayed; a newly created run stores only the canonical
pair. This intentionally preserves the old writer's comparison domain for already
recorded operations. If two formerly accepted OIDC pairs produced the same delimited
legacy string, the old row cannot be disambiguated after the fact and retains its
pre-upgrade replay behavior. New rows cannot have that collision because their two
fields are compared separately.

### Migration ownership and ordering

Issue #35 requires schema migrations for all four databases. Its migration:

1. adds the three Document actor fields, backfills the legacy payload, validates the
   three-field state, and removes `documents.created_by`;
2. converts Document owner issuer and subject fields to the canonical binary mapping;
3. adds the three access-grant actor fields, backfills their legacy payload, validates
   the state, and removes `document_access_grants.created_by`; and
4. converts access-grant principal issuer and subject fields to the canonical binary
   mapping while preserving the v1 `CreatedBy` response projection described above.

Document ingestion and access-grant creation then write only canonical actor pairs.

Issue #36 also requires schema migrations for all four databases and must deliver
issue #26 in the same migration series. That series performs the following order:

1. validate the InnoDB prerequisites and make the Parse Run table `DYNAMIC` on MySQL
   and MariaDB;
2. add the three Parse Run actor fields with the binary mappings above;
3. backfill and validate the legacy payload state;
4. drop `ux_parse_runs_idempotency` once;
5. remove `parse_runs.created_by` and apply the ordinal `idempotency_key` mapping;
6. create `ux_parse_runs_idempotency` once over
   `(created_by_issuer, created_by_subject, document_id, idempotency_key)`; and
7. add the legacy replay helper index and switch creation and replay lookup to
   canonical-pair-plus-legacy-payload lookup.

Thus #35 has its own Document schema change, #36 has a Parse Run schema change, and
#26 is implemented by #36's one index/collation migration rather than by a second
index rebuild.

## Alternatives Considered

### Fixed-width derived key

The evaluated form was a 32-byte SHA-256 digest of a version byte, a four-byte
big-endian issuer length, the exact canonical issuer bytes, a four-byte big-endian
subject length, and the exact canonical subject bytes. It would use ordinal binary
comparison and reduce the current InnoDB index budget to `32 + 36 + 256 = 324`
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

- Every accepted actor identity, including a subject containing NUL, fits the audit
  tables and the Parse Run idempotency index on every supported database.
- Audit rows remain reversibly readable through the defined ASCII decoding, and exact
  identity equality is not reduced to a hash-collision assumption.
- OIDC issuer and subject are no longer flattened into an ambiguous string.
- Legacy audit text, including old non-ASCII Document actors and 773-character SQLite
  Parse Run actors, and Idempotency-Key replay remain available without inventing
  missing identity information.
- Access-grant actor auditing uses the same representation without breaking its v1
  `CreatedBy` field.
- #26 and #36 have one explicit index rebuild and one collation contract.

### Trade-offs

- Documents, Parse Runs, and access grants each require three replacement actor fields
  and migrations on all four database providers. Existing owner and grant-principal
  pairs also move to the binary mapping.
- Parse Run replay temporarily needs a second lookup representation for rows created
  before the migration. That compatibility path cannot be removed while such rows
  are retained.
- Historical Document rows whose old actor omitted an OIDC issuer remain historical
  strings; the migration cannot recover an issuer that was never stored.
- MySQL and MariaDB deployments require InnoDB `DYNAMIC` row format with a page size
  of at least 16 KiB.
