using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EzOdata.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSystemSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    occurred_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    request_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    category = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    service_id = table.Column<long>(type: "INTEGER", nullable: true),
                    app_id = table.Column<long>(type: "INTEGER", nullable: true),
                    user_id = table.Column<long>(type: "INTEGER", nullable: true),
                    role_id = table.Column<long>(type: "INTEGER", nullable: true),
                    resource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    detail_json = table.Column<string>(type: "TEXT", nullable: false),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    service_id = table.Column<long>(type: "INTEGER", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_policies",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    scope_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    scope_id = table.Column<long>(type: "INTEGER", nullable: true),
                    window_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    max_requests = table.Column<int>(type: "INTEGER", nullable: false),
                    verbs = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    row_version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_limit_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    bypass_data_rules = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    row_version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "services",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    connector_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    connection_encrypted = table.Column<string>(type: "TEXT", nullable: false),
                    connection_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    connection_display = table.Column<string>(type: "TEXT", nullable: false),
                    options_json = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    status_detail = table.Column<string>(type: "TEXT", nullable: true),
                    schema_refresh_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    row_version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_services", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    value_json = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_system_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    failed_login_count = table.Column<int>(type: "INTEGER", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    row_version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    role_id = table.Column<long>(type: "INTEGER", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    allowed_origins_json = table.Column<string>(type: "TEXT", nullable: true),
                    require_user_session = table.Column<bool>(type: "INTEGER", nullable: false),
                    mcp_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    row_version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_apps_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_service_access",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    role_id = table.Column<long>(type: "INTEGER", nullable: false),
                    service_id = table.Column<long>(type: "INTEGER", nullable: true),
                    resource_pattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    verbs = table.Column<int>(type: "INTEGER", nullable: false),
                    row_filter = table.Column<string>(type: "TEXT", nullable: true),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    effect = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_service_access", x => x.id);
                    table.ForeignKey(
                        name: "FK_role_service_access_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schema_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    service_id = table.Column<long>(type: "INTEGER", nullable: false),
                    version_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    snapshot_json = table.Column<string>(type: "TEXT", nullable: false),
                    table_count = table.Column<int>(type: "INTEGER", nullable: false),
                    view_count = table.Column<int>(type: "INTEGER", nullable: false),
                    introspected_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    is_current = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_schema_snapshots_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    family_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_by_ip = table.Column<string>(type: "TEXT", nullable: true),
                    user_agent = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    role_id = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false),
                    key_prefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    key_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_policies",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    role_service_access_id = table.Column<long>(type: "INTEGER", nullable: false),
                    field_pattern = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    mask_value = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_policies", x => x.id);
                    table.ForeignKey(
                        name: "FK_field_policies_role_service_access_role_service_access_id",
                        column: x => x.role_service_access_id,
                        principalTable: "role_service_access",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_app_id",
                table: "api_keys",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_name",
                table: "apps",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_role_id",
                table: "apps",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_app_id_occurred_at",
                table: "audit_events",
                columns: new[] { "app_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_service_id_occurred_at",
                table: "audit_events",
                columns: new[] { "service_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_field_policies_role_service_access_id",
                table: "field_policies",
                column: "role_service_access_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_kind_status",
                table: "jobs",
                columns: new[] { "kind", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_rate_limit_policies_scope_type_scope_id",
                table: "rate_limit_policies",
                columns: new[] { "scope_type", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_family_id",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_service_access_role_id_service_id",
                table: "role_service_access",
                columns: new[] { "role_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "IX_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schema_snapshots_service_id",
                table: "schema_snapshots",
                column: "service_id",
                unique: true,
                filter: "is_current = 1");

            migrationBuilder.CreateIndex(
                name: "IX_services_name",
                table: "services",
                column: "name",
                unique: true,
                filter: "is_deleted = 0");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "field_policies");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "rate_limit_policies");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "schema_snapshots");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "role_service_access");

            migrationBuilder.DropTable(
                name: "services");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
