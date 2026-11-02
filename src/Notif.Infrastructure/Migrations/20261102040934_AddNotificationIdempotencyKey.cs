using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notif.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notification_logs_ApplicationId_EventType_StatusSnapshot",
                table: "notification_logs");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "notification_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            // Existing rows were deduped by status snapshot: keep that exact behaviour
            // after switching the unique index to the explicit idempotency key.
            migrationBuilder.Sql(
                "UPDATE notification_logs SET \"IdempotencyKey\" = lower(\"StatusSnapshot\") WHERE \"IdempotencyKey\" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_notification_logs_ApplicationId_EventType_IdempotencyKey",
                table: "notification_logs",
                columns: new[] { "ApplicationId", "EventType", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notification_logs_ApplicationId_EventType_IdempotencyKey",
                table: "notification_logs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "notification_logs");

            // Event-scoped job notifications can legitimately repeat a status for one
            // aggregate; rolling back may require cleaning up notification history first.
            migrationBuilder.CreateIndex(
                name: "IX_notification_logs_ApplicationId_EventType_StatusSnapshot",
                table: "notification_logs",
                columns: new[] { "ApplicationId", "EventType", "StatusSnapshot" },
                unique: true);
        }
    }
}
