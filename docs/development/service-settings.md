# Service Settings

- Status: Implementation note
- Last updated: 2026-08-08

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

Settings are an allowlist. A key that could reach the store without appearing in the catalog would change behaviour no test covers, and would let one compromised administrator session reach configuration that was never meant to be writable from a browser, including paths and credentials. Unknown keys answer `404`.

The catalog restates each default because several of these keys are absent from `appsettings.json` and take their default from the options class. Tests assert the restatements against those classes, so a default cannot drift into a claim about behaviour that is not true.

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
| `POST` | `/api/v1/admin/system/restart` | Stop the Host so its supervisor restarts it |

All are administrator-only, and the writes require antiforgery validation.

## Remaining Work

- secret-valued settings, which need encryption through Data Protection before any are published;
- Storage, OIDC, and business-database settings, which is the point at which a connection test and a recovery path for a bad value become necessary;
- change listeners for the remaining keys, so fewer settings need a restart at all.
