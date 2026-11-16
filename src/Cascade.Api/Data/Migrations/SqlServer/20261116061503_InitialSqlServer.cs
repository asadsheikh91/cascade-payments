using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cascade.Api.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    idempotency_key = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    merchant_reference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    payment_method = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    refunded_amount = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    psp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    psp_reference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    decline_code = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_payments_refunded_within_amount", "refunded_amount >= 0 AND refunded_amount <= amount");
                });

            migrationBuilder.CreateTable(
                name: "processed_shopify_events",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    webhook_id = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_shopify_events", x => x.idempotency_key);
                });

            migrationBuilder.CreateTable(
                name: "shopify_orders",
                columns: table => new
                {
                    shop_domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    financial_status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    fulfillment_status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    cancelled = table.Column<bool>(type: "bit", nullable: false),
                    total_price = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    shopify_updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_source = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shopify_orders", x => new { x.shop_domain, x.id });
                });

            migrationBuilder.CreateTable(
                name: "shopify_reconciliation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    shop_domain = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    since = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    fetched = table.Column<int>(type: "int", nullable: false),
                    repaired = table.Column<int>(type: "int", nullable: false),
                    error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shopify_reconciliation_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_cursors",
                columns: table => new
                {
                    name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    value = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_cursors", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    webhook_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    event_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    topic = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    shop_domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    api_version = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    triggered_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.webhook_id);
                    table.CheckConstraint("ck_webhook_deliveries_payload_is_json", "ISJSON(payload) = 1");
                });

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    psp = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    psp_reference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    decline_code = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_attempts_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    psp_reference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "fk_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_payment_id_sequence",
                table: "payment_attempts",
                columns: new[] { "payment_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_created_at",
                table: "payments",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_payments_idempotency_key",
                table: "payments",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_id",
                table: "refunds",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_status_next_attempt_at_triggered_at",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at", "triggered_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_attempts");

            migrationBuilder.DropTable(
                name: "processed_shopify_events");

            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropTable(
                name: "shopify_orders");

            migrationBuilder.DropTable(
                name: "shopify_reconciliation_runs");

            migrationBuilder.DropTable(
                name: "sync_cursors");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "payments");
        }
    }
}
