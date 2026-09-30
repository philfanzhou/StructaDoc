using Microsoft.EntityFrameworkCore;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Adapters.Persistence;
using StructaDoc.Application.Settings;

namespace StructaDoc.Host.Health;

/// <summary>
/// The one input ServiceMantle's readiness endpoint has: the state of this service, sampled
/// per request. Readiness means the administration area is reachable (the control-plane
/// database answers) and documents and parsing are available (the business database carries no
/// startup fault and still answers), which is the same contract the health checks it replaces
/// published.
///
/// The three snapshot fields map as follows, fixed by this implementation and locked by tests:
/// <see cref="ServiceHealthSnapshot.Phase"/> is always <see cref="ServiceStartupPhase.Completed"/>
/// once the host is running. StructaDoc has no administrator-created-yet state that should fail
/// readiness: a container that has finished starting is ready to be routed to, and reporting a
/// setup phase here would make an unconfigured container fail its HEALTHCHECK and enter a restart
/// loop. <see cref="ServiceHealthSnapshot.MigrationStatus"/> is <see cref="ServiceMigrationReadinessState.Failed"/>
/// exactly when a stored business-database configuration could not be prepared at startup, and
/// <see cref="ServiceDatabaseReadinessState.Reachable"/> only when both databases answer a
/// read-only probe. Deployment-pinned configuration failure still stops the host, so it never
/// appears here.
///
/// Probing is read-only, cancellation-aware, and bounded by the library's probe timeout. A probe
/// that throws is an internal failure, not a state: it is left to propagate so the endpoint fails
/// closed with `health.probe_failed`. The error codes below carry no connection string, path, or
/// exception text.
/// </summary>
public sealed class StructaDocHealthSnapshotSource(
    ControlPlaneDbContext controlPlane,
    StructaDocDbContext businessDatabase,
    SettingsStartupFault settingsStartupFault) : IServiceHealthSnapshotSource
{
    public const string ControlPlaneUnreachableErrorCode = "structadoc.control_plane.unreachable";
    public const string DatabaseStartupFaultErrorCode = "structadoc.database.startup_fault";
    public const string DatabaseUnreachableErrorCode = "structadoc.database.unreachable";

    public async ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var controlPlaneReachable = await controlPlane.Database.CanConnectAsync(cancellationToken);
        var businessDatabaseReachable = await businessDatabase.Database.CanConnectAsync(
            cancellationToken);
        var startupFault = settingsStartupFault.DetailFor(SettingCatalog.DatabaseSection) is not null;

        var migrationStatus = startupFault
            ? ServiceMigrationReadinessState.Failed
            : ServiceMigrationReadinessState.Succeeded;
        var databaseStatus = controlPlaneReachable && businessDatabaseReachable
            ? ServiceDatabaseReadinessState.Reachable
            : ServiceDatabaseReadinessState.Unreachable;

        return new ServiceHealthSnapshot(
            ServiceStartupPhase.Completed,
            migrationStatus,
            databaseStatus,
            ErrorCodeFor(startupFault, controlPlaneReachable, businessDatabaseReachable));
    }

    // A stable precedence, because a deployment can fail more than one way at once and the body
    // carries only one code: the recorded startup fault is the reason the database was never
    // prepared, so it wins; an unreachable database is otherwise reported for whichever side
    // failed to answer.
    private static string? ErrorCodeFor(
        bool startupFault,
        bool controlPlaneReachable,
        bool businessDatabaseReachable) => startupFault
            ? DatabaseStartupFaultErrorCode
            : !controlPlaneReachable
                ? ControlPlaneUnreachableErrorCode
                : businessDatabaseReachable
                    ? null
                    : DatabaseUnreachableErrorCode;
}
