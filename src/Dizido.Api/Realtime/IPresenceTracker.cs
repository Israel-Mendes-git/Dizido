using StackExchange.Redis;

namespace Dizido.Api.Realtime;

public interface IPresenceTracker
{
    /// <summary>Registra uma conexão. Devolve true se o usuário ficou online agora (primeira conexão).</summary>
    Task<bool> ConnectedAsync(Guid userId, string connectionId);

    /// <summary>Remove uma conexão. Devolve true se o usuário ficou offline (era a última).</summary>
    Task<bool> DisconnectedAsync(Guid userId, string connectionId);

    Task<bool> IsOnlineAsync(Guid userId);

    Task<IReadOnlyList<Guid>> FilterOnlineAsync(IReadOnlyList<Guid> userIds);

    /// <summary>Conexões abertas de um usuário (pode ter várias: navegador, desktop...).</summary>
    Task<IReadOnlyList<string>> GetConnectionsAsync(Guid userId);
}

/// <summary>Presença mantida no Redis.</summary>
/// <remarks>
/// <para>
/// Por que Redis e não um dicionário em memória: com duas instâncias da API atrás de um load
/// balancer, cada uma enxergaria só as próprias conexões, e metade dos usuários apareceria
/// offline para a outra metade. O Redis é o estado compartilhado.
/// </para>
/// <para>
/// Cada usuário tem um <c>SET</c> de connectionIds — a mesma pessoa pode estar com o app aberto
/// no navegador e no desktop. Ela só fica offline quando o conjunto esvazia.
/// </para>
/// <para>
/// O TTL é a rede de segurança: se um processo morrer sem chamar <c>OnDisconnectedAsync</c>, as
/// conexões dele evaporam sozinhas em vez de deixar o usuário eternamente "online". O cliente
/// renova o TTL por heartbeat.
/// </para>
/// </remarks>
public sealed class RedisPresenceTracker(IConnectionMultiplexer redis) : IPresenceTracker
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly IDatabase _db = redis.GetDatabase();

    private static RedisKey Key(Guid userId) => $"presence:{userId}";

    public async Task<bool> ConnectedAsync(Guid userId, string connectionId)
    {
        var key = Key(userId);

        var added = await _db.SetAddAsync(key, connectionId);
        await _db.KeyExpireAsync(key, Ttl);

        // Ficou online agora só se esta é a única conexão dele.
        return added && await _db.SetLengthAsync(key) == 1;
    }

    public async Task<bool> DisconnectedAsync(Guid userId, string connectionId)
    {
        var key = Key(userId);

        await _db.SetRemoveAsync(key, connectionId);

        return await _db.SetLengthAsync(key) == 0;
    }

    public Task<bool> IsOnlineAsync(Guid userId) => _db.KeyExistsAsync(Key(userId));

    public async Task<IReadOnlyList<Guid>> FilterOnlineAsync(IReadOnlyList<Guid> userIds)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        // Uma ida ao Redis para todos os ids, não uma por id. O mesmo raciocínio do N+1
        // no banco vale aqui: latência de rede multiplicada pelo número de itens.
        var batch = _db.CreateBatch();
        var tasks = userIds.Select(id => batch.KeyExistsAsync(Key(id))).ToArray();
        batch.Execute();

        var results = await Task.WhenAll(tasks);

        return [.. userIds.Where((_, i) => results[i])];
    }

    public async Task<IReadOnlyList<string>> GetConnectionsAsync(Guid userId)
    {
        var members = await _db.SetMembersAsync(Key(userId));

        return [.. members.Select(m => m.ToString())];
    }

    public Task RenewAsync(Guid userId) => _db.KeyExpireAsync(Key(userId), Ttl);
}
