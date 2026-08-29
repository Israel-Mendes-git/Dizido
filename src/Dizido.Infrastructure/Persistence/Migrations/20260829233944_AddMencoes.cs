using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dizido.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMencoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "Mentions",
                table: "messages",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            // Índice GIN sobre o array.
            //
            // É o que faz "as mensagens que citam esta pessoa" ser uma busca por índice em vez
            // de uma varredura da tabela inteira. O operador de contenção do Postgres (@>) usa
            // este tipo de índice; um B-tree comum não serviria, porque indexaria o array
            // inteiro como um valor único — e ninguém procura por "o array exato [a, b]".
            migrationBuilder.Sql("""
                CREATE INDEX ix_messages_mencoes ON messages USING GIN ("Mentions");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_messages_mencoes;");

            migrationBuilder.DropColumn(
                name: "Mentions",
                table: "messages");
        }
    }
}
