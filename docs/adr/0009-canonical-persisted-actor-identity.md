# ADR-0009: Persist Actor Identities as Structured Pairs

- Status: Accepted
- Date: 2026-08-25
- Supersedes in part: [ADR-0004](./0004-relational-database-portability.md),
  [ADR-0008](./0008-api-client-resource-isolation.md)

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
| Local administrator session (`SubjectTypes.Administrator`) | ASCII bytes of `structadoc:administrator` | ASCII bytes of the administrator ID as a lowercase `D`-format UUID |

Each accepted ASCII code unit is stored as its one-byte value, including `0x00`.
`created_by_issuer` accepts at most 512 bytes and `created_by_subject` accepts at most
255 bytes. This encoding is one-to-one and reversible; it does not rely on a database
text type being able to represent every accepted subject.

Each affected audit record also has a nullable binary `created_by_legacy` field.
Documents and Parse Runs allow at most 1024 bytes; access grants allow at most 4096
bytes so every value admitted by their former `varchar(1024)` schema remains
representable. Documents and Parse Runs allow exactly one of these states:

- both canonical fields are present and `created_by_legacy` is absent;
- both canonical fields are absent and `created_by_legacy` is present; or
- all three fields are absent for historical or system-authored data for which no
  actor was recorded.

Access grants allow only the first two states. Their actor was required before this
change and every access-grant write has an authenticated actor, so an access grant
must have either a canonical pair or a legacy payload. The all-absent state is
forbidden by its schema constraint.

New authenticated writes always use the canonical pair. They never write the legacy
field.

Actor classification uses `StructaDocClaimTypes.SubjectType`, not an authorization
predicate. Only a `SubjectTypes.Administrator` principal maps to
`structadoc:administrator`. A `SubjectTypes.User` principal always maps to the OIDC
row using its external issuer and subject, even when its OIDC role adds the
administrator claim and authorization treats it as an administrator. Consequently,
`IsAdministrator` must not select the persisted actor representation.

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
characters (`0x21` through `0x7e`). As part of this transition it also begins
comparing ordinally: `BINARY` on SQLite, `C` on PostgreSQL, and the `ascii` character
set with `ascii_bin` collation on MySQL and MariaDB. This preserves the existing
public API input contract while establishing the documented ordinal persistence
contract; this ADR does not broaden the accepted Idempotency-Key input.

Operators and application code decode each canonical byte as the same-valued ASCII
code unit. API-client subjects resolve through `api_clients` in the business
database. Local-administrator subjects resolve through `admin_users` in the separate
control-plane database. An OIDC pair resolves through the configured identity
provider; StructaDoc does not create a shadow user directory merely for audit
display. Restoring a business database alongside a different control-plane database
does not damage the stored actor bytes, but a local-administrator subject cannot be
resolved unless the matching control-plane record was restored too. Deployments that
require that resolution must back up and restore the two databases as a matched set.

`DocumentAccessGrantResponse.CreatedBy` remains a required string in `/api/v1`.
For a canonical row it is projected in the existing display form:
`oidc:{issuer}|{subject}`, `api-client:{subject}`, or
`administrator:{subject}`. For a legacy row it is the decoded former value. This
keeps the v1 DTO backward compatible while making only the persisted representation
canonical; the flattened response is not an identity key. Because an access grant
cannot use the all-absent state, this projection is always non-null as required by the
v1 contract.

`DocumentAccessGrantResponse.Issuer` and `Subject` also remain required strings.
Their canonical bytes are decoded one-for-one to the same-valued ASCII code units;
this includes a subject byte `0x00`, which the JSON serializer emits as the escaped
character `\u0000`. The binary persistence change therefore does not narrow or
nullify either v1 field.

The canonical Document model's optional `createdBy` field remains the logical actor
audit fact; removing the old scalar database column does not remove that model
field. New rows represent it with the canonical pair and migrated rows represent it
with the opaque legacy payload. It remains internal in the current `/api/v1`
Document projection: `DocumentResponse` does not expose `createdBy`. This is distinct
from the access-grant DTO above, whose existing required scalar projection must be
preserved.

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
support boundary. The application-managed migration path first determines whether a
pending migration creates or rebuilds an index that depends on that boundary. If no
such migration is pending, startup does not reject a database merely because the
server's current default row format later changed.

The reusable preflight and application-managed execution point are owned by
[issue #43](https://github.com/philfanzhou/StructaDoc/issues/43). The equivalent
one-shot external migration entry point is owned by
[issue #45](https://github.com/philfanzhou/StructaDoc/issues/45).

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

Legacy payloads do not participate in the canonical unique index because no new
legacy rows are written and the nullable canonical columns mean that adding the
payload would not make legacy and canonical rows conflict on any supported database.
Replay first narrows candidates through a non-unique
`(document_id, idempotency_key)` index, whose maximum InnoDB data length is
`36 + 256 = 292` bytes, and then compares the legacy payload exactly.

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

Provider-specific conversion and validation details are owned by
[issues #49](https://github.com/philfanzhou/StructaDoc/issues/49) and
[#50](https://github.com/philfanzhou/StructaDoc/issues/50) for Documents,
[issues #51](https://github.com/philfanzhou/StructaDoc/issues/51) and
[#52](https://github.com/philfanzhou/StructaDoc/issues/52) for access grants, and
[issue #36](https://github.com/philfanzhou/StructaDoc/issues/36) for Parse Runs.

The compatibility field covers the actual old writer domains rather than trusting
unenforced EF Core length metadata:

- the Document writer can persist 255 .NET characters, including non-ASCII values;
  valid Unicode of that length needs at most 765 UTF-8 bytes;
- the Parse Run and access-grant writers can emit
  `oidc:` + 512 issuer bytes + `|` + 255 subject bytes, or 773 UTF-8 bytes;
- the former access-grant `varchar(1024)` schema on server databases admits as many
  as 4096 UTF-8 bytes even though the application writer emits less; and
- administrator and API-client forms are shorter.

The 1024-byte Document and Parse Run fields and 4096-byte access-grant field therefore
preserve both the application-produced values and the full declared server-side
access-grant domain. A migration fails with an actionable error only for invalid
Unicode, an unsupported SQLite database encoding, or a value beyond the applicable
former column domain.

This preserves the exact historical audit text for operators without pretending it
contains information the old writer never stored. Legacy payloads are compatibility
records, not a fourth kind of identity accepted for new writes.

Existing Document-owner and access-grant-principal pairs are already canonical
identity facts, so their migration changes only the physical encoding: each accepted
ASCII code unit becomes the same-valued byte. The conversion, index ordering, and
upgrade-test details are owned by
[issue #35](https://github.com/philfanzhou/StructaDoc/issues/35).

Idempotency-Key replay recorded before the upgrade remains addressable. For a request
that has an Idempotency-Key, Parse Run creation checks both the request's canonical
structured pair and the UTF-8 legacy payload for the exact actor string produced by
the old Parse Run writer. An existing legacy match is replayed; a newly created run
stores only the canonical pair. Pre-insert ordering, concurrency handling, and
cutover details are owned by
[issue #36](https://github.com/philfanzhou/StructaDoc/issues/36).

Legacy replay is byte-exact after the upgrade. In particular, MySQL and MariaDB rows
created under a case- or accent-insensitive table collation no longer replay when a
later request changes the case or accents of the actor display string or changes the
case of the Idempotency-Key. The same bytes still replay, but aliases that only the
former collation treated as equal can create a new Parse Run. This is an intentional
compatibility narrowing that restores the already documented ordinal identity and
visible-ASCII Idempotency-Key contracts; the migration does not claim to emulate an
arbitrary historical database collation. If two formerly accepted OIDC pairs
produced the same delimited legacy string, an exact legacy match still cannot be
disambiguated after the fact. New rows cannot have that collision because their two
fields are compared separately.

### Migration ownership

[Issue #48](https://github.com/philfanzhou/StructaDoc/issues/48) owns the shared
canonical actor mapping. [Issues #49](https://github.com/philfanzhou/StructaDoc/issues/49)
and [#50](https://github.com/philfanzhou/StructaDoc/issues/50) own the coordinated
Document schema changes; [issues #51](https://github.com/philfanzhou/StructaDoc/issues/51)
and [#52](https://github.com/philfanzhou/StructaDoc/issues/52) own the coordinated
access-grant schema changes. [Issue #36](https://github.com/philfanzhou/StructaDoc/issues/36)
owns the Parse Run schema change, and
[issue #26](https://github.com/philfanzhou/StructaDoc/issues/26) is implemented by
#36's one index/collation migration rather than by a second index rebuild. The reusable
InnoDB preflight and its application-managed integration are owned by
[issue #43](https://github.com/philfanzhou/StructaDoc/issues/43); the external migration
entry point is owned by [issue #45](https://github.com/philfanzhou/StructaDoc/issues/45).

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
- Legacy audit text, including old non-ASCII Document actors, 773-character SQLite
  Parse Run actors, and the full former 4096-byte server-side access-grant domain,
  remains available without inventing missing identity information; exact
  Idempotency-Key replay remains available too.
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
- Legacy replay is exact after the migration; case- or accent-insensitive aliases
  that matched only because of a former MySQL or MariaDB collation are not preserved.
- Resolving a local-administrator actor after restore requires the matching
  control-plane backup; API-client resolution remains within the business database.
- Historical Document rows whose old actor omitted an OIDC issuer remain historical
  strings; the migration cannot recover an issuer that was never stored.
- MySQL and MariaDB deployments require InnoDB `DYNAMIC` row format with a page size
  of at least 16 KiB.
- SQLite actor-replacement migrations require a UTF-8 database and fail before
  mutation for another database encoding or malformed UTF-8 source text.
