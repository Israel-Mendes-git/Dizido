using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dizido.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>Troca a URL do avatar por uma referência ao anexo.</summary>
    /// <remarks>
    /// O EF avisa que esta migration pode perder dados, e é verdade: a coluna AvatarUrl é
    /// removida. Aqui é seguro porque ela nunca foi preenchida — o SetAvatar existia no
    /// domínio desde a Fase 6 e não tinha endpoint que chegasse até ele.
    /// <para>
    /// Se houvesse dados, o caminho seria outro: adicionar a coluna nova, migrar o conteúdo
    /// num passo intermediário, e só então remover a antiga — em três deploys, não em um.
    /// </para>
    /// </remarks>
    public partial class AvatarComoAnexo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "conversations");

            migrationBuilder.AddColumn<Guid>(
                name: "AvatarAttachmentId",
                table: "conversations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarAttachmentId",
                table: "conversations");

            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "conversations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }
    }
}
