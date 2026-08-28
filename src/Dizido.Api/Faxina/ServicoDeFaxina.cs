using Dizido.Domain.Enums;
using Dizido.Infrastructure.Persistence;
using Dizido.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dizido.Api.Faxina;

/// <summary>
/// Apaga periodicamente o que só cresce: tokens vencidos e anexos abandonados.
/// </summary>
/// <remarks>
/// <para>
/// Duas tabelas do Dizido crescem sem nunca diminuir sozinhas. A de refresh tokens ganha uma
/// linha por login e nunca perde nenhuma, mesmo depois de o token vencer. A de anexos ganha
/// uma linha a cada pedido de upload, inclusive os que o usuário desistiu no meio — e essas
/// nunca serão usadas nem apagadas por ninguém.
/// </para>
/// <para>
/// Nenhuma das duas causa problema em um mês. As duas causam em um ano, e o sintoma é lento
/// demais para alguém associar à causa: as consultas ficam um pouco mais lentas a cada semana.
/// </para>
/// <para>
/// <b>Um BackgroundService, e não um cron externo.</b> Assim a faxina viaja junto da aplicação:
/// quem instalar o Dizido não precisa lembrar de configurar mais nada, e não existe a
/// possibilidade de a versão do código e a do script de limpeza divergirem.
/// </para>
/// </remarks>
internal sealed partial class ServicoDeFaxina(
    IServiceProvider servicos,
    IConnectionMultiplexer redis,
    TimeProvider clock,
    ILogger<ServicoDeFaxina> log) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(6);

    /// <summary>Quanto tempo um token vencido ainda é guardado.</summary>
    /// <remarks>
    /// Não é zero de propósito. Um token vencido ainda serve para investigar: se alguém
    /// reclamar que a sessão caiu sozinha, a linha diz quando venceu e se foi revogada por
    /// detecção de reuso. Trinta dias é tempo de sobra para essa conversa acontecer.
    /// </remarks>
    private static readonly TimeSpan CarenciaDeTokens = TimeSpan.FromDays(30);

    /// <summary>Quanto tempo um upload não confirmado ainda pode ser confirmado.</summary>
    /// <remarks>
    /// A URL de upload vale dez minutos. Uma hora é margem generosa para relógios
    /// desencontrados e para um cliente que ficou pendurado terminando o envio.
    /// </remarks>
    private static readonly TimeSpan CarenciaDeAnexos = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Espera antes da primeira rodada: durante a subida, a aplicação tem coisa melhor a
        // fazer com a conexão do banco do que varrer tabela.
        await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken);

        using var relogio = new PeriodicTimer(Intervalo, clock);

        do
        {
            try
            {
                await LimparAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Uma falha na faxina não pode derrubar a aplicação: ela é manutenção, não
                // funcionalidade. Registra e tenta de novo daqui a seis horas.
                LogFalhou(log, e);
            }
        }
        while (await relogio.WaitForNextTickAsync(stoppingToken));
    }

    private async Task LimparAsync(CancellationToken ct)
    {
        // Só uma instância faz a faxina de cada vez.
        //
        // Com três réplicas, as três acordariam juntas e rodariam os mesmos DELETE sobre as
        // mesmas linhas. Não corromperia nada — o resultado é idempotente —, mas seria três
        // vezes o trabalho e uma boa chance de deadlock no Postgres.
        //
        // SET com NX e expiração é o cadeado mais simples que existe: quem conseguir criar a
        // chave trabalha, os outros vão embora. A expiração garante que uma instância que
        // morrer no meio não deixe o cadeado trancado para sempre.
        var cadeado = redis.GetDatabase();

        if (!await cadeado.StringSetAsync(
                "dizido:faxina", Environment.MachineName,
                expiry: Intervalo - TimeSpan.FromMinutes(5),
                when: When.NotExists))
        {
            return;
        }

        using var escopo = servicos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<DizidoDbContext>();
        var storage = escopo.ServiceProvider.GetRequiredService<IObjectStorage>();

        var agora = clock.GetUtcNow();

        var tokens = await db.RefreshTokens
            .Where(t => t.ExpiresAt < agora - CarenciaDeTokens)
            .ExecuteDeleteAsync(ct);

        var anexos = await LimparAnexosAsync(db, storage, agora, ct);

        if (tokens > 0 || anexos > 0)
        {
            LogFaxina(log, tokens, anexos);
        }
    }

    /// <summary>
    /// Remove os anexos que ficaram pendentes e os bytes que porventura chegaram.
    /// </summary>
    /// <remarks>
    /// O objeto sai do storage antes da linha do banco. Na ordem inversa, um processo que
    /// morresse no meio deixaria o arquivo no bucket sem nenhuma linha apontando para ele —
    /// invisível para a próxima faxina, e cobrado para sempre.
    /// </remarks>
    private async Task<int> LimparAnexosAsync(
        DizidoDbContext db, IObjectStorage storage, DateTimeOffset agora, CancellationToken ct)
    {
        var abandonados = await db.Attachments
            .Where(a => a.Status == AttachmentStatus.Pending && a.CreatedAt < agora - CarenciaDeAnexos)

            // Um teto por rodada: se algo der muito errado e sobrarem milhares, é melhor
            // limpar aos poucos do que segurar uma transação enorme.
            .Take(500)
            .ToListAsync(ct);

        foreach (var anexo in abandonados)
        {
            try
            {
                await storage.DeleteAsync(anexo.StorageKey, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // O storage pode estar fora do ar, ou o objeto pode nunca ter existido — que
                // é justamente o caso mais comum aqui. Nenhum dos dois impede de apagar a
                // linha; deixá-la manteria o problema de crescimento sem resolver nada.
                LogObjetoNaoRemovido(log, anexo.StorageKey, e);
            }

            db.Attachments.Remove(anexo);
        }

        await db.SaveChangesAsync(ct);

        return abandonados.Count;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Faxina: {Tokens} token(s) vencido(s) e {Anexos} anexo(s) abandonado(s) removidos")]
    private static partial void LogFaxina(ILogger logger, int tokens, int anexos);

    [LoggerMessage(Level = LogLevel.Error, Message = "A faxina falhou. Nova tentativa na próxima rodada.")]
    private static partial void LogFalhou(ILogger logger, Exception erro);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Não foi possível remover o objeto {Chave} do storage; a linha será apagada mesmo assim")]
    private static partial void LogObjetoNaoRemovido(ILogger logger, string chave, Exception erro);
}
