using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StructaDoc.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AttributeApiClientDocumentOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Documents an API client uploaded were recorded without an owner, because a client
            // used to reach every Document in the deployment and had no need of one. It no longer
            // does, so an unattributed Document would become invisible to the client that created
            // it. `created_by` already names that client, and is the only record of which one, so
            // ownership is recovered from it.
            //
            // `substr` with a start position is the one spelling SQLite, PostgreSQL, MySQL, and
            // MariaDB agree on. 'api-client:' is eleven characters, so the client ID starts at
            // twelve. Documents uploaded by an administrator keep no owner: an administrator is not
            // a workspace principal and reaches every Document in any case.
            migrationBuilder.Sql("""
                update documents
                set owner_issuer = 'structadoc:api-client',
                    owner_subject = substr(created_by, 12)
                where owner_issuer is null
                  and owner_subject is null
                  and created_by like 'api-client:%'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back restores the schema this ran against, and in that schema an API client
            // reaches every Document regardless of owner. Ownership recorded for one is therefore
            // meaningless rather than merely unused, and leaving it behind would outlive the roll
            // back as a filter on the OIDC identities that share the column.
            migrationBuilder.Sql("""
                update documents
                set owner_issuer = null,
                    owner_subject = null
                where owner_issuer = 'structadoc:api-client'
                """);
        }
    }
}
