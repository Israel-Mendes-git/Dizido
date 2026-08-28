using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api;

/// <summary>
/// Aplica as migrations pendentes e encerra. É o "job de migração" do deploy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que não no start normal.</b> A tentação é chamar <c>Migrate()</c> logo depois do
/// <c>builder.Build()</c> e nunca mais pensar no assunto. O problema aparece na primeira vez
/// que duas instâncias sobem juntas — num deploy sem interrupção, por exemplo: as duas
/// disparam a mesma migration ao mesmo tempo, e uma delas encontra a tabela já criada pela
/// outra no meio do caminho. O resultado é uma instância morta com erro de esquema, e o banco
/// num estado que ninguém pediu.
/// </para>
/// <para>
/// Aqui a migração acontece só quando alguém pede, com <c>--aplicar-migracoes</c>. No compose
/// de produção isso é um serviço próprio, que roda uma vez, e do qual a API depende: ela nem
/// começa a subir antes de ele terminar com sucesso.
/// </para>
/// <para>
/// O código de saída importa: é como o orquestrador sabe se pode seguir com o deploy ou se
/// precisa parar tudo. Uma migração que falha em silêncio, devolvendo zero, libera a subida de
/// uma versão nova contra um banco velho.
/// </para>
/// </remarks>
internal static partial class Migracoes
{
    public const string Argumento = "--aplicar-migracoes";

    public static async Task<int> AplicarAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migracoes");

        using var escopo = app.Services.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<DizidoDbContext>();

        try
        {
            var pendentes = (await db.Database.GetPendingMigrationsAsync()).ToList();

            if (pendentes.Count == 0)
            {
                LogSemPendencias(log);
                return 0;
            }

            // O string.Join fica fora da chamada de propósito. O analisador CA1873 reprova
            // qualquer expressão potencialmente cara passada como argumento de log — porque o
            // método gerado só decide se vai formatar depois de receber os argumentos, e a
            // conta já teria sido feita. Aqui o custo é irrisório (isto roda uma vez por
            // deploy), mas a regra vale por ser uniforme.
            var nomes = string.Join(", ", pendentes);

            LogAplicando(log, pendentes.Count, nomes);

            await db.Database.MigrateAsync();

            LogAplicadas(log);
            return 0;
        }
        catch (Exception e)
        {
            // O stack trace é obrigatório aqui, mais do que em qualquer outro lugar: quando a
            // migração falha, o deploy está parado e alguém precisa saber exatamente qual
            // comando SQL não passou.
            LogFalhou(log, e);
            return 1;
        }
    }

    // O padrão [LoggerMessage] gera um delegate tipado em tempo de compilação, que só formata a
    // mensagem se o nível estiver ativo. É o que os analisadores do projeto exigem (CA1848), e
    // a razão é real: a sobrecarga com params object[] aloca um array em toda chamada, mesmo
    // quando o log está desligado.

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Nenhuma migration pendente. O banco já está no esquema atual.")]
    private static partial void LogSemPendencias(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Aplicando {Quantidade} migration(s): {Migrations}")]
    private static partial void LogAplicando(ILogger logger, int quantidade, string migrations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations aplicadas.")]
    private static partial void LogAplicadas(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "A migração falhou. O deploy não deve prosseguir.")]
    private static partial void LogFalhou(ILogger logger, Exception erro);
}
