using System.Security.Claims;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using StructaDoc.Adapters.ControlPlane;
using StructaDoc.Application.Authentication;

namespace StructaDoc.Host.Auditing;

/// <summary>
/// The logger category shared by management audit record points. Endpoints hosting record points
/// are static classes, which cannot name a logger category themselves.
/// </summary>
public sealed record ManagementAuditRecordPoints;

/// <summary>
/// The Host-side management audit record points: the operator an authenticated administrator
/// presents, and the degrading save used by record points whose own operation has already
/// succeeded when the audit row is written. An audit failure is never silent — it is logged at
/// error level — but it does not retroactively fail an operation that committed.
/// </summary>
public static class HostManagementAudit
{
    /// <summary>
    /// The audit operator for an authenticated administrator: the account's stable identifier and
    /// its username, attributed to the interactive administrator source.
    /// </summary>
    public static ManagementAuditOperator AdministratorOperator(ClaimsPrincipal user)
    {
        return StructaDocManagementAudit.AdministratorOperator(
            user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            user.FindFirstValue(StructaDocClaimTypes.Username));
    }

    /// <summary>
    /// The audit operator for the administrator whose account a mutating endpoint acts on, when
    /// that account is not the signed-in caller.
    /// </summary>
    public static ManagementAuditOperator AdministratorOperator(
        string administratorId,
        string? username) =>
        StructaDocManagementAudit.AdministratorOperator(administratorId, username);

    /// <summary>
    /// Records an audit row, logging a failure rather than failing the record point's operation.
    /// </summary>
    /// <param name="recorder">The audit recorder.</param>
    /// <param name="logger">The record point's logger.</param>
    /// <param name="auditEvent">The event to record.</param>
    /// <param name="recordPoint">
    /// The record point's name for the log entry, for example "administrator login".
    /// </param>
    /// <param name="cancellationToken">Propagates caller cancellation.</param>
    public static async Task RecordAsync(
        StructaDocManagementAuditRecorder recorder,
        ILogger logger,
        ManagementAuditEvent auditEvent,
        string recordPoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await recorder.RecordAsync(auditEvent, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(
                error,
                "The management audit record for the {RecordPoint} record point could not be written.",
                recordPoint);
        }
    }

    /// <summary>The client address a record point attributes its event to, when one is present.</summary>
    public static string? ClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();
}
