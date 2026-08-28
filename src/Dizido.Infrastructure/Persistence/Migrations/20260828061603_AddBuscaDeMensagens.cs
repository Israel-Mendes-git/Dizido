using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dizido.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Busca full-text nas mensagens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Escrita à mão, e não gerada pelo modelo, de propósito. A coluna <c>busca</c> é um
    /// <c>tsvector</c>, tipo do Postgres que o <c>Dizido.Domain</c> não pode conhecer — ele não
    /// referencia o Npgsql, e é isso que permite testar as regras de negócio sem banco. Mapear
    /// a coluna numa propriedade da entidade quebraria essa regra por uma funcionalidade que
    /// é, no fim, um detalhe de consulta.
    /// </para>
    /// <para>
    /// <b>GENERATED ALWAYS ... STORED</b> faz o banco manter a coluna sozinho, a cada INSERT e
    /// UPDATE. A alternativa — a aplicação calcular e gravar — exigiria lembrar disso em todo
    /// caminho que escreve mensagem, e o dia em que alguém esquecesse produziria mensagens
    /// invisíveis para a busca, sem erro nenhum.
    /// </para>
    /// <para>
    /// A configuração <c>'portuguese'</c> traz o radical das palavras: procurar por "correr"
    /// encontra "correndo" e "correu". Com <c>'simple'</c> — o padrão — só casaria a palavra
    /// exata, e a busca pareceria quebrada para quem escreve português.
    /// </para>
    /// </remarks>
    public partial class AddBuscaDeMensagens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE messages
                ADD COLUMN busca tsvector
                GENERATED ALWAYS AS (to_tsvector('portuguese', coalesce("Body", ''))) STORED;
                """);

            // GIN é o índice para busca full-text: ele indexa cada palavra apontando para as
            // linhas que a contêm — o inverso de um índice comum. Sem ele, cada busca varreria
            // a tabela inteira aplicando to_tsvector linha por linha.
            migrationBuilder.Sql("""
                CREATE INDEX ix_messages_busca ON messages USING GIN (busca);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_messages_busca;");
            migrationBuilder.Sql("ALTER TABLE messages DROP COLUMN IF EXISTS busca;");
        }
    }
}
