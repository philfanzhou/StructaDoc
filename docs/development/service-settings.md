# Service Settings

- Status: Implementation note
- Last updated: 2026-08-09

## Purpose

A deployment is expected to be operated entirely from the browser. Settings that would otherwise require editing an environment variable and recreating the container are stored in the control plane and changed under `/admin` instead. Configuration files and environment variables remain the way a deployment pins a value; they are not the only way to set one.

## Where Settings Live

Stored settings are rows in the control-plane SQLite database, alongside administrator accounts and for the same reason: they must be readable before anything an administrator configures is reachable. A row exists only for a value an administrator chose. An absent row means the shipped default applies, which is not the same as a row holding that default, and clearing a setting deletes the row rather than writing an empty value.

## Precedence

1. an environment variable or command-line argument, when present;
2. a stored setting;
3. the value the service ships with.

Precedence is decided explicitly rather than by where a configuration source lands in a list. Host builders do not agree on that order, and the test host appends sources after the application has configured itself, so an ordering-based rule would mean one thing in production and another under test. Stored settings are layered above the application configuration, and any key the deployment pins is left out of that layer entirely: it keeps winning because nothing is ever put above it.

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

Settings are an allowlist. A key that could reach the store without appearing in the catalog would change behaviour no test covers, and would let one compromised administrator session reach configuration that was never meant to be writable from a browser, including paths and credentials. Unknown keys answer `404`.

The catalog restates each default because several of these keys are absent from `appsettings.json` and take their default from the options class. Tests assert the restatements against those classes, so a default cannot drift into a claim about behaviour that is not true. An empty default means the service ships without a value for that key rather than with an empty one, which is the honest description of an authority nothing supplies.

`Oidc:Scopes` is an array and cannot be expressed as one key and one value, so it is not settable. `CallbackPath` and `SignedOutCallbackPath` are not settable either, because they are addresses registered at the identity provider rather than choices. All three are reported by `GET /api/v1/admin/settings/oidc` so an administrator can see them.

Nothing under `Authentication` is settable. Those options are read from the raw configuration before the store is opened, because the Data Protection key ring they locate is what decrypts the stored secrets; a settable key there would be read too early to have any effect. An architecture test holds that.

An address is stored without its trailing slash. An authority written with one and the issuer a provider reports without one are the same address, and the sign-in middleware compares them literally.

## Secrets

A secret is encrypted with the Data Protection key ring in `/data/keys`, the same one that already protects Provider credentials. The control-plane database sits beside the rest of a deployment's data and travels with every backup, so a client secret written there in the clear would travel with it.

The read API reports only whether a secret is set. Its value never reaches a browser, so an administration session that is read cannot give up a credential the reader did not write. Clearing it deletes the row, as for any other setting.

Losing or replacing the key ring makes a stored secret unreadable rather than breaking the deployment. It is dropped at startup and recorded as a fault, reported as set so it can be written over, and never reported as pending a restart, because restarting would drop it again.

## Failing to Start

A value written from a browser can be wrong in ways a configuration file cannot, because nobody is watching a container log when an administrator presses save. Settings are also written one key at a time, so a combination such as `Oidc:Enabled` without `Oidc:Authority` is reachable in an ordinary order of work and no single write can see it coming.

Refusing to start on that would take away the only surface such a deployment could be fixed from. A stored section that fails validation is dropped instead, the service starts without it, and `GET /api/v1/admin/settings/oidc` reports what was rejected. The whole section goes rather than the failing key alone: half a configuration is harder to reason about than none. The stored values are still reported so an administrator can see what they wrote; the fault is what says they are not in effect.

The distinction is the source of the value, not the error. Anything the deployment pins still stops the service, because whoever set it has a command line. This applies to the `Oidc` section today; nothing else settable can fail this way yet.

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
| `POST` | `/api/v1/admin/system/restart` | Stop the Host so its supervisor restarts it |

All are administrator-only, and the writes require antiforgery validation. The discovery test counts as a write for that purpose: it makes the service fetch an address the caller chose. See [User Workspace and OIDC](./user-workspace-oidc.md) for what the test does and does not establish.

## Remaining Work

- Storage and business-database settings, which need a connection test of their own and a recovery path that keeps `/admin` usable while the business database is unreachable;
- change listeners for the remaining keys, so fewer settings need a restart at all;
- rate limiting on the discovery test, which is the one settings endpoint that makes an outbound request.
