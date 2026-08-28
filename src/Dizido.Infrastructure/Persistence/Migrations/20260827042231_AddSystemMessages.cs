using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dizido.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SystemEvent",
                table: "messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SystemTargetId",
                table: "messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "SystemEvent",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "SystemTargetId",
                table: "messages");
        }
    }
}
