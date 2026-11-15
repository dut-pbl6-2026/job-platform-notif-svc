using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notif.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "telegram_subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_subscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_telegram_subscriptions_ChatId",
                table: "telegram_subscriptions",
                column: "ChatId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telegram_subscriptions");
        }
    }
}
