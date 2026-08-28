using System.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Dizido.Api.Observabilidade;

/// <summary>
/// Logs, traces e métricas — as três formas de descobrir o que o servidor está fazendo quando
/// ninguém está olhando.
/// </summary>
internal static class Observabilidade
{
    /// <summary>
    /// Registra o Serilog no lugar do logger padrão.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>O ponto do log estruturado.</b> Escrever <c>$"Mensagem em {id}"</c> produz uma frase;
    /// escrever <c>"Mensagem em {ConversaId}"</c> com o valor à parte produz uma frase <b>e</b>
    /// um campo pesquisável. A diferença aparece no dia em que você precisa de "todos os erros
    /// desta conversa nas últimas duas horas": com texto, é grep e sorte; com campo, é filtro.
    /// </para>
    /// <para>
    /// <b>O que nunca entra no log.</b> Corpo de mensagem, nome de arquivo anexado, email. Num
    /// app de mensagens o log é a maior porta de vazamento que existe: ele é copiado para
    /// sistemas de busca, fica meses retido e é lido por muito mais gente do que o banco.
    /// Identificadores, sim; conteúdo, não.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>Não escreva comentários dentro de <c>Serilog:MinimumLevel:Override</c>.</b> O truque
    /// de usar chaves <c>"//"</c> como comentário em JSON — que funciona nas outras seções deste
    /// projeto — quebra aqui: o Serilog lê <b>cada chave</b> do bloco como um namespace, e o
    /// texto do comentário vira um seletor de nível inexistente. A aplicação não sobe, com
    /// <c>No LoggingLevelSwitch has been declared with name "..."</c> repetindo o comentário.
    /// </para>
    /// <para>
    /// Os níveis existem para calar o ruído: o ASP.NET e o EF Core sozinhos produzem mais linhas
    /// do que a aplicação inteira. Em desenvolvimento o SQL do EF fica em Information, porque é
    /// como se descobre um N+1 antes de ele virar lentidão; em produção seria ruído caro.
    /// </para>
    /// </remarks>
    public static void UsarSerilog(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((servicos, log) => log
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(servicos)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("aplicacao", "Dizido.Api")
            .Enrich.WithProperty("ambiente", builder.Environment.EnvironmentName)
            .WriteTo.Console(EscolherFormato(builder.Environment)));
    }

    /// <summary>
    /// Uma linha por requisição, em vez das quatro que o ASP.NET emite por padrão.
    /// </summary>
    /// <remarks>
    /// O <c>/health</c> é excluído do log: o orquestrador o consulta a cada poucos segundos,
    /// e mantê-lo encheria o armazenamento de log com ruído que esconde o que importa.
    /// </remarks>
    public static void UsarLogDeRequisicoes(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} respondeu {StatusCode} em {Elapsed:0.0} ms";

            options.GetLevel = (contexto, _, erro) =>
                erro is not null ? LogEventLevel.Error
                : contexto.Response.StatusCode >= 500 ? LogEventLevel.Error
                : contexto.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        });
    }

    /// <summary>
    /// Traces e métricas via OpenTelemetry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Por que trace importa aqui.</b> Uma mensagem com anexo passa por API, Postgres, MinIO
    /// e SignalR. Quando alguém reclama que "demora para enviar foto", o log diz que cada peça
    /// respondeu; só o trace diz <b>qual delas</b> levou os dois segundos.
    /// </para>
    /// <para>
    /// <b>O exportador só liga se houver para onde exportar.</b> Sem
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> definido, o OpenTelemetry fica coletando e
    /// descartando — o que em desenvolvimento evita uma tentativa de conexão falhando a cada
    /// poucos segundos, e em produção significa que basta definir a variável para começar a
    /// enxergar, sem recompilar nada.
    /// </para>
    /// </remarks>
    public static void UsarOpenTelemetry(this WebApplicationBuilder builder)
    {
        var destino = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var temColetor = !string.IsNullOrWhiteSpace(destino);

        var recurso = ResourceBuilder.CreateDefault()
            .AddService("dizido-api", serviceVersion: VersaoDaAplicacao)
            .AddAttributes([new KeyValuePair<string, object>("ambiente", builder.Environment.EnvironmentName)]);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("dizido-api", serviceVersion: VersaoDaAplicacao))
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(recurso)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Sem isto, o trace fica dominado por milhares de spans de health check,
                        // e o que interessa some no meio.
                        o.Filter = contexto => !contexto.Request.Path.StartsWithSegments("/health");
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()

                    // O Npgsql publica spans por conta própria nesta ActivitySource — é o que
                    // faz cada consulta ao banco aparecer dentro do trace da requisição, com o
                    // tempo que levou.
                    .AddSource("Npgsql");

                // Não há instrumentação estável do StackExchange.Redis (só beta), então as
                // chamadas de presença não aparecem no trace. Aceitável: elas são um GET/SET
                // e não são o gargalo provável. Reavaliar quando o pacote sair de beta.

                if (temColetor)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(recurso)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()

                    // Coletor de lixo, memória, threads. É o que responde "o processo está
                    // sofrendo?" antes de o usuário perceber.
                    .AddRuntimeInstrumentation()

                    // As nossas: mensagens, anexos, conexões abertas.
                    .AddMeter(DizidoMetrics.Nome);

                if (temColetor)
                {
                    metrics.AddOtlpExporter();
                }
            });

        builder.Services.AddSingleton<DizidoMetrics>();
    }

    /// <summary>
    /// Em desenvolvimento, texto para gente ler. Em produção, uma linha de JSON por evento.
    /// </summary>
    /// <remarks>
    /// A saída bonita com cores é ótima no terminal e péssima no agregador de logs, que precisa
    /// dos campos separados para poder filtrar. Fora de desenvolvimento, JSON compacto.
    /// </remarks>
    private static Serilog.Formatting.ITextFormatter EscolherFormato(IHostEnvironment ambiente) =>
        ambiente.IsDevelopment()
            ? new Serilog.Formatting.Display.MessageTemplateTextFormatter(
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            : new CompactJsonFormatter();

    private static string VersaoDaAplicacao =>
        FileVersionInfo.GetVersionInfo(typeof(Observabilidade).Assembly.Location).ProductVersion
        ?? "desconhecida";
}
