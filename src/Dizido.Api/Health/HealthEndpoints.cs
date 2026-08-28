using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dizido.Api.Health;

/// <summary>
/// Os dois endpoints que um orquestrador consulta, e por que são dois.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>/health</c> — está vivo?</b> Não toca em dependência nenhuma. Se ele falhar, a resposta
/// certa é <b>reiniciar o processo</b>.
/// </para>
/// <para>
/// <b><c>/health/ready</c> — consegue atender?</b> Confere banco, cache e storage. Se ele falhar,
/// a resposta certa é <b>tirar esta instância do balanceador</b> até melhorar.
/// </para>
/// <para>
/// Juntar os dois num endpoint só é o erro clássico, e o estrago é concreto: o Postgres cai, o
/// health check falha, o orquestrador reinicia todas as instâncias — que sobem, não conseguem
/// conectar, e são reiniciadas de novo. Um problema de banco vira um <i>crash loop</i> da
/// aplicação inteira, e o log fica cheio de reinícios em vez do erro de conexão que interessa.
/// </para>
/// </remarks>
internal static class HealthEndpoints
{
    /// <summary>Marca os checks que dependem de algo externo, para o liveness poder ignorá-los.</summary>
    public const string TagDeProntidao = "pronto";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Predicate que não casa com nada: o endpoint responde 200 se o processo está de pé e
        // conseguindo atender requisições HTTP. É exatamente essa a pergunta do liveness.
        routes.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false,
        }).AllowAnonymous().ExcludeFromDescription();

        routes.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(TagDeProntidao),
            ResponseWriter = EscreverAsync,
        }).AllowAnonymous().ExcludeFromDescription();

        return routes;
    }

    /// <summary>
    /// Escreve o resultado em JSON, com um item por dependência.
    /// </summary>
    /// <remarks>
    /// O formato padrão devolve a palavra "Healthy" e mais nada — o que responde "está tudo bem?"
    /// mas não "o que quebrou?". Quem está de plantão às três da manhã precisa da segunda resposta.
    /// <para>
    /// A <b>descrição</b> de cada check entra; a <b>exceção</b>, não. Stack trace num endpoint
    /// anônimo entrega a estrutura interna do sistema e a versão das bibliotecas a quem pedir.
    /// O erro completo vai para o log, que é lugar autenticado.
    /// </para>
    /// </remarks>
    private static async Task EscreverAsync(HttpContext context, HealthReport relatorio)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var corpo = new
        {
            estado = relatorio.Status.ToString(),
            duracaoMs = relatorio.TotalDuration.TotalMilliseconds,
            checagens = relatorio.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    estado = e.Value.Status.ToString(),
                    duracaoMs = e.Value.Duration.TotalMilliseconds,
                    descricao = e.Value.Description,
                }),
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(corpo, JsonDaResposta), context.RequestAborted);
    }

    private static readonly JsonSerializerOptions JsonDaResposta = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
