using Dizido.Api.Realtime;

namespace Dizido.Api.Tests;

/// <summary>
/// Substitui o <c>RedisPresenceTracker</c> nos testes: ninguém está online, nunca.
/// </summary>
/// <remarks>
/// <para>
/// O banco é real de propósito (Testcontainers) — é lá que moram as regras que estes testes
/// verificam. O Redis não: presença é estado volátil de quem está com o app aberto, e nenhuma
/// regra de autorização depende dela. Subir um segundo contêiner para responder sempre "lista
/// vazia" só deixaria a suíte mais lenta.
/// </para>
/// <para>
/// A consequência visível: o campo <c>IsOnline</c> das respostas vem sempre <c>false</c> nos
/// testes. Nenhuma asserção depende dele.
/// </para>
/// </remarks>
internal sealed class PresencaEmMemoria : IPresenceTracker
{
    public Task<bool> ConnectedAsync(Guid userId, string connectionId) => Task.FromResult(true);

    public Task<bool> DisconnectedAsync(Guid userId, string connectionId) => Task.FromResult(true);

    public Task<bool> IsOnlineAsync(Guid userId) => Task.FromResult(false);

    public Task<IReadOnlyList<Guid>> FilterOnlineAsync(IReadOnlyList<Guid> userIds) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<string>> GetConnectionsAsync(Guid userId) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
