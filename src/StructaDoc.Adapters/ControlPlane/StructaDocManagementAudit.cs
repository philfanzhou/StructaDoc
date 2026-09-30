using ServiceMantle.Audit;
using ServiceMantle.Persistence.Relational.Stores;

namespace StructaDoc.Adapters.ControlPlane;

/// <summary>
/// StructaDoc's management audit vocabulary on top of the ServiceMantle audit domain: the
/// product-specific actions and target types the record points use, following the library's
/// well-known constants wherever one fits. Everything an audit field may carry is defined here as
/// an explicit non-sensitive allowlist; credentials, passwords, connection strings, and setup codes
/// never enter an audit field.
/// </summary>
public static class StructaDocManagementAudit
{
    /// <summary>Actions for the record points StructaDoc defines itself.</summary>
    public static class Actions
    {
        /// <summary>The first administrator claimed setup and created the account.</summary>
        public static ManagementAuditAction SetupClaimed { get; } =
            ManagementAuditAction.Parse("structadoc.setup.claimed");

        /// <summary>An administrator account was created.</summary>
        public static ManagementAuditAction AdministratorAccountCreated { get; } =
            ManagementAuditAction.Parse("structadoc.administrator_account_created");

        /// <summary>An administrator changed their own password.</summary>
        public static ManagementAuditAction AdministratorPasswordChanged { get; } =
            ManagementAuditAction.Parse("structadoc.administrator_password_changed");

        /// <summary>An administrator reset another account's password.</summary>
        public static ManagementAuditAction AdministratorPasswordReset { get; } =
            ManagementAuditAction.Parse("structadoc.administrator_password_reset");

        /// <summary>An administrator account was enabled.</summary>
        public static ManagementAuditAction AdministratorEnabled { get; } =
            ManagementAuditAction.Parse("structadoc.administrator_enabled");

        /// <summary>An administrator account was disabled.</summary>
        public static ManagementAuditAction AdministratorDisabled { get; } =
            ManagementAuditAction.Parse("structadoc.administrator_disabled");

        /// <summary>An administrator account was deleted.</summary>
        public static ManagementAuditAction AdministratorDeleted { get; } =
            ManagementAuditAction.Parse("structadoc.administrator_deleted");

        /// <summary>An API client was created.</summary>
        public static ManagementAuditAction ApiClientCreated { get; } =
            ManagementAuditAction.Parse("structadoc.api_client_created");

        /// <summary>An API client was revoked.</summary>
        public static ManagementAuditAction ApiClientRevoked { get; } =
            ManagementAuditAction.Parse("structadoc.api_client_revoked");

        /// <summary>A Provider configuration was created.</summary>
        public static ManagementAuditAction ProviderConfigCreated { get; } =
            ManagementAuditAction.Parse("structadoc.provider_config_created");

        /// <summary>A Provider configuration got a new immutable version.</summary>
        public static ManagementAuditAction ProviderConfigChanged { get; } =
            ManagementAuditAction.Parse("structadoc.provider_config_changed");

        /// <summary>A Provider configuration was enabled.</summary>
        public static ManagementAuditAction ProviderConfigEnabled { get; } =
            ManagementAuditAction.Parse("structadoc.provider_config_enabled");

        /// <summary>A Provider configuration was disabled.</summary>
        public static ManagementAuditAction ProviderConfigDisabled { get; } =
            ManagementAuditAction.Parse("structadoc.provider_config_disabled");

        /// <summary>An administrator asked the service to restart.</summary>
        public static ManagementAuditAction RestartRequested { get; } =
            ManagementAuditAction.Parse("structadoc.restart_requested");
    }

    /// <summary>Target types for the record points StructaDoc defines itself.</summary>
    public static class TargetTypes
    {
        /// <summary>An administrator account in the control plane.</summary>
        public static ManagementAuditTargetType AdministratorAccount { get; } =
            ManagementAuditTargetType.Parse("structadoc.administrator_account");

        /// <summary>An API client in the business database.</summary>
        public static ManagementAuditTargetType ApiClient { get; } =
            ManagementAuditTargetType.Parse("structadoc.api_client");

        /// <summary>A Provider configuration in the business database.</summary>
        public static ManagementAuditTargetType ProviderConfig { get; } =
            ManagementAuditTargetType.Parse("structadoc.provider_config");
    }

    /// <summary>
    /// The administrator identity for an audit operator, from the account's stable identifier and
    /// username. Neither value is sensitive; the display name carries the username because that is
    /// what an administrator reading the trail recognizes.
    /// </summary>
    public static ManagementAuditOperator AdministratorOperator(
        string administratorId,
        string? username) =>
        ManagementAuditOperator.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            administratorId,
            username);

    /// <summary>
    /// The operator for a sign-in attempt whose identity could not be established: the only
    /// attribution available is the username that was submitted.
    /// </summary>
    public static ManagementAuditOperator LoginAttemptOperator(string? submittedUsername) =>
        ManagementAuditOperator.Create(
            WellKnownManagementAuditOperatorSources.Anonymous,
            submittedUsername is null ? null : $"login-attempt:{submittedUsername}");
}

/// <summary>
/// The management audit record point surface: stages audit rows through the library's
/// stage-only writer on the control plane. <see cref="StageAsync"/> joins a record point's own
/// control-plane unit of work, while <see cref="RecordAsync"/> commits the row on its own for
/// record points whose own changes live in another database or have already committed.
/// </summary>
public sealed class StructaDocManagementAuditRecorder(
    IManagementAuditWriter auditWriter,
    ControlPlaneDbContext controlPlane)
{
    /// <summary>
    /// Stages an audit row on the control plane without saving. A record point that commits its
    /// own changes through the same control-plane context calls this before its
    /// <c>SaveChangesAsync</c>, so the row and the change commit and roll back together.
    /// </summary>
    public ValueTask<ManagementAuditRecord> StageAsync(
        ManagementAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        auditWriter.RecordAsync(auditEvent, cancellationToken);

    /// <summary>
    /// Stages and saves an audit row in one step, for record points whose own unit of work is
    /// elsewhere. The row is persisted on the control plane in its own save.
    /// </summary>
    public async Task RecordAsync(
        ManagementAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await auditWriter.RecordAsync(auditEvent, cancellationToken);
        await controlPlane.SaveChangesAsync(cancellationToken);
    }
}
