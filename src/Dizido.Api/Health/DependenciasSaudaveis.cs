using Dizido.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Dizido.Api.Health;

/// <summary>O Redis responde?</summary>
/// <remarks>
/// Um <c>PING</c>, e nada além disso. A tentação é fazer o check gravar e ler uma chave para
/// "testar de verdade" — mas aí o health check passa a escrever no banco a cada poucos segundos,
/// vindo de cada instância, e vira carga própria. O que interessa saber é se a conexão está de pé.
/// </remarks>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latencia = await redis.GetDatabase().PingAsync();

            return HealthCheckResult.Healthy(
                $"respondeu em {latencia.TotalMilliseconds:0} ms",
                new Dictionary<string, object> { ["latenciaMs"] = latencia.TotalMilliseconds });
        }
        catch (RedisException e)
        {
            // Sem Redis, o app continua servindo mensagens — o que se perde é presença e o
            // backplane entre instâncias. Degraded, e não Unhealthy: tirar a instância do
            // balanceador por causa disso deixaria o serviço todo fora do ar por um problema
            // que só afeta uma funcionalidade.
            return HealthCheckResult.Degraded("Redis fora do ar: presença e tempo real entre instâncias ficam prejudicados.", e);
        }
    }
}

/// <summary>O object storage responde e o bucket existe?</summary>
/// <remarks>
/// Aqui a verificação é a mais barata possível: perguntar por um objeto que não existe. O
/// storage responde "não achei" rápido, e isso já prova que o endereço está certo, que as
/// credenciais valem e que o bucket está lá. Listar o bucket seria caro e desnecessário.
/// </remarks>
internal sealed class StorageHealthCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.DescribeAsync("health/sonda", cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded("Storage fora do ar: anexos não sobem nem carregam.", e);
        }
    }
}
