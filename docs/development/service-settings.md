# Service Settings

- Status: Implementation note
- Last updated: 2026-08-10

## Purpose

A deployment is expected to be operated entirely from the browser. Settings that would otherwise require editing an environment variable and recreating the container are stored in the control plane and changed under `/admin` instead. Configuration files and environment variables remain the way a deployment pins a value; they are not the only way to set one.

## Where Settings Live

Stored settings are rows in the control-plane SQLite database, alongside administrator accounts and for the same reason: they must be readable before anything an administrator configures is reachable. A row exists only for a value an administrator chose. An absent row means the shipped default applies, which is not the same as a row holding that default, and clearing a setting deletes the row rather than writing an empty value.

## Precedence

1. an environment variable or command-line argument, when present;
2. a stored setting;
3. the container image's own default, when running inside the image;
4. the value the service ships with.

Precedence is decided explicitly rather than by where a configuration source lands in a list. Host builders do not agree on that order, and the test host appends sources after the application has configured itself, so an ordering-based rule would mean one thing in production and another under test. Stored settings are layered above the application configuration, and any key the deployment pins is left out of that layer entirely: it keeps winning because nothing is ever put above it.

Level 3 is decided the same way, and had to be: `appsettings.Container.json` first tried to take its place by source position, and the web host — which reads environment variables both before and after `appsettings.json` and then chains its host configuration on at the end — has no position that is above the repository's defaults and below the deployment's at once. The file landed under `appsettings.json`, and the image started against a read-only `/app/data`. It is now applied key by key, skipping every key an environment variable or argument supplied. See [Single Container](../deployment/single-container.md) for what the image puts there.

A pinned setting is reported as managed externally and cannot be written through the API, which answers `409`. Storing a value the service would never read would report a change that did not happen.

## Settable Keys

| Key | Type | Effect |
|---|---|---|
| `Worker:ExecutionEnabled` | boolean | Applies immediately |
| `Worker:MaxConcurrency` | integer, 1–64 | Restart |
| `Documents:UploadApiEnabled` | boolean | Restart |
| `Documents:MaxUploadBytes` | integer, 1024–8 GiB | Restart |
| `Oidc:Enabled` | boolean | Restart |
| `Oidc:Authority` | address | Restart |
| `Oidc:ClientId` | text | Restart |
| `Oidc:ClientSecret` | secret | Restart |
| `Oidc:RequireHttpsMetadata` | boolean | Restart |
| `Oidc:NameClaim`, `Oidc:EmailClaim`, `Oidc:RoleClaim`, `Oidc:AdministratorRole` | text | Restart |
| `Storage:Provider` | `Local` or `S3` | Restart |
| `Storage:RootPath` | text | Restart |
| `Storage:ServiceUrl` | address | Restart |
| `Storage:Region`, `Storage:Bucket`, `Storage:Prefix` | text | Restart |
| `Storage:AccessKey`, `Storage:SecretKey` | secret | Restart |
| `Storage:ForcePathStyle` | boolean | Restart |
| `Database:Provider` | `Sqlite`, `PostgreSql`, `MySql`, or `MariaDb` | Restart |
| `Database:ConnectionString` | secret | Restart |
| `Database:ServerVersion` | text | Restart |

`Storage:Provider` and `Database:Provider` are closed sets. A value outside one is refused by the write rather than at the next start, which is the only moment an administrator is still looking at what they typed. The list of accepted spellings is reported with the setting, so the web interface offers a choice instead of asking anyone to guess, and an architecture test holds it against what the options classes accept.

`Storage:AccessKey` is a secret alongside `Storage:SecretKey`. An access key identifies a credential rather than being the whole of it, but storage credentials do not go to browsers here, and whether one is set is all an administrator needs to manage it.

`Database:ConnectionString` is a secret because it usually carries a password. It is the one setting whose catalog default is deliberately empty rather than restated: a default here would be a credential compiled into the image, and what actually applies with no stored row comes from the configuration the build ships.

Settings are an allowlist. A key that could reach the store without appearing in the catalog would change behaviour no test covers, and would let one compromised administrator session reach configuration that was never meant to be writable from a browser, including paths and credentials. Unknown keys answer `404`.

The catalog restates each default because several of these keys are absent from `appsettings.json` and take their default from the options class. Tests assert the restatements against those classes, so a default cannot drift into a claim about behaviour that is not true. An empty default means the service ships without a value for that key rather than with an empty one, which is the honest description of an authority nothing supplies.

`Oidc:Scopes` is an array and cannot be expressed as one key and one value, so it is not settable. `CallbackPath` and `SignedOutCallbackPath` are not settable either, because they are addresses registered at the identity provider rather than choices. All three are reported by `GET /api/v1/admin/settings/oidc` so an administrator can see them.

Nothing under `Authentication` is settable. Those options are read from the raw configuration before the store is opened, because the Data Protection key ring they locate is what decrypts the stored secrets; a settable key there would be read too early to have any effect. An architecture test holds that.

Nothing under `ReverseProxy` is settable either, for a different reason. It names the peer allowed to say what scheme, host, and address a request really had, which is a fact about the network the container was placed in; an administrator reaches the service through that proxy and cannot see what is in front of it, and a wrong answer lets a caller choose its own apparent address. It stays with whoever placed the container, and a test holds it out of this catalog. See [Behind a Reverse Proxy](../deployment/single-container.md#behind-a-reverse-proxy).

An address is stored without its trailing slash. An authority written with one and the issuer a provider reports without one are the same address, and the sign-in middleware compares them literally.

## Secrets

A secret is encrypted with the Data Protection key ring in `/data/keys`, the same one that already protects Provider credentials. The control-plane database sits beside the rest of a deployment's data and travels with every backup, so a client secret written there in the clear would travel with it.

The read API reports only whether a secret is set. Its value never reaches a browser, so an administration session that is read cannot give up a credential the reader did not write. Clearing it deletes the row, as for any other setting.

Losing or replacing the key ring makes a stored secret unreadable rather than breaking the deployment. It is dropped at startup and recorded as a fault, reported as set so it can be written over, and never reported as pending a restart, because restarting would drop it again.

## Failing to Start

A value written from a browser can be wrong in ways a configuration file cannot, because nobody is watching a container log when an administrator presses save. Settings are also written one key at a time, so a combination such as `Oidc:Enabled` without `Oidc:Authority` is reachable in an ordinary order of work and no single write can see it coming.

Refusing to start on that would take away the only surface such a deployment could be fixed from. A stored section that fails validation is dropped instead, the service starts without it, and `GET /api/v1/admin/settings/oidc` reports what was rejected. The whole section goes rather than the failing key alone: half a configuration is harder to reason about than none. The stored values are still reported so an administrator can see what they wrote; the fault is what says they are not in effect.

The distinction is the source of the value, not the error. Anything the deployment pins still stops the service, because whoever set it has a command line.

`Oidc`, `Storage`, and `Database` are the recoverable sections. Each is dropped to the shipped default when a stored value fails to bind or validate, and each records why. An architecture test holds that every recoverable section is one the catalog can write to, since a section no browser can produce is not one that needs rescuing. More than one can be wrong at once — moving a deployment to object storage and an external database in the same sitting is an ordinary way to do that — so faults are kept per section rather than one at a time.

The business database goes further, because a wrong value there is not always a value that fails to validate. A connection string can be perfectly well formed and point at a server that is not there, credentials that are refused, or a database this build cannot migrate, and none of that is visible until the service starts. Refusing to start on it would take away the administration area, which is the only place it can be corrected from, so a startup migration that fails against a *stored* configuration is recorded and the service starts without a usable business database. Administrator sign-in, settings, storage, and the database panel all still work, because they run on the control plane. Readiness still fails, so nothing routes real traffic to a service that cannot store a document. A database the deployment pinned still stops startup exactly as before.

The administration page loads its panels independently rather than together for the same reason: Providers and API clients live in the business database, and if one failed read blanked the page, the settings needed to repair it would go with it.

## Taking Effect

Options are bound once at startup, so a stored value reaches the running service only through a change listener. `Worker:ExecutionEnabled` has one: the execution Worker consults a gate on every cycle rather than reading the flag at startup, so turning parsing on and off never needs a restart. Runs already claimed are not interrupted when it closes.

Everything else needs a restart, and the API reports that from what actually happened rather than from the catalog flag, so a setting that lost its listener says a restart is needed instead of claiming an effect it did not have. `GET /api/v1/admin/settings` reports `isPendingRestart` for a stored value the running process has not picked up.

## Restart

`POST /api/v1/admin/system/restart` stops the Host. Nothing in the image starts it again: what brings the service back is the container restart policy, so a container started without one stays down until it is started by hand. The web interface says so before asking for confirmation, and the response repeats it. Run the container with `--restart unless-stopped` if administrators are expected to use this.

The Host exits cleanly, with status `0`, so `on-failure` does not restart it. `unless-stopped` and `always` do.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/admin/settings` | Current value, source, and pending state of every settable key |
| `PUT` | `/api/v1/admin/settings` | Set or clear one key |
| `GET` | `/api/v1/admin/settings/oidc` | What sign-in is running, what was rejected at startup, and the addresses to register |
| `POST` | `/api/v1/admin/settings/oidc/test` | Fetch an authority's discovery document and check it |
| `GET` | `/api/v1/admin/settings/storage` | Which storage the running service uses, and what was rejected at startup |
| `POST` | `/api/v1/admin/settings/storage/test` | Write and remove one probe object at a candidate location |
| `GET` | `/api/v1/admin/settings/database` | Which database the running service uses, whether it answers, and whether it needs migrating |
| `POST` | `/api/v1/admin/settings/database/test` | Open a candidate database and read its migration history |
| `POST` | `/api/v1/admin/system/restart` | Stop the Host so its supervisor restarts it |

All are administrator-only, and the writes require antiforgery validation. A connection test counts as a write for that purpose: it makes the service reach an address the caller chose.

## Testing Before Committing

Storage and the database each take effect only after a restart, so a wrong value is discovered by a service that does not come back. Both can therefore be tried first, and both tests take the same shape: every field is optional and an omitted one falls back to what is in force. An administrator can check a bucket name without retyping a Secret Key the service never sends back, or test exactly what is already saved.

The storage probe writes a small object under the configured prefix and removes it again. Listing is not enough: a bucket that lists but refuses writes accepts every upload attempt and fails each one, and a local path that exists inside a read-only container looks fine until the first document arrives. The client is built the same way the running service builds its own, so a probe that passes describes the deployment that would actually run.

The database probe connects and reads migration history. It creates nothing, because a connection string pointing at the wrong database must not leave StructaDoc tables behind in it. It separates a database that is current from one that answers but has not been migrated yet, since the second is fine and the first restart fixes it.

Both report a stable code the web interface translates, plus a bounded detail. A driver's message is the useful part of a failed connection, but it is written by something that was handed a credential and may quote it back, so any message containing the submitted connection string or storage key is dropped rather than repeated to a browser.

## Remaining Work

- change listeners for the remaining keys, so fewer settings need a restart at all;
- rate limiting on the connection tests, which are the settings endpoints that make outbound requests;
- moving existing objects and rows when storage or the database changes, which today is left where it belongs — with whoever is doing the migration.
