using Dizido.Api.Realtime;
using Dizido.Contracts.Conversations;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Persistence;
using Dizido.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Conversations;

/// <summary>
/// Monta o <see cref="ConversationResponse"/> — nomes dos membros, quem está online, e a URL
/// do avatar do grupo.
/// </summary>
/// <remarks>
/// <para>
/// Existe porque essa montagem estava copiada em três lugares (lista de conversas, operações
/// de grupo e sincronização). Enquanto era só juntar nomes, a duplicação passava; quando o
/// avatar virou uma referência a anexo — que exige assinar uma URL —, manter as três cópias
/// em dia deixou de ser razoável.
/// </para>
/// <para>
/// Todas as consultas são em lote, por conversa nenhuma: com vinte conversas de cinco membros,
/// são três idas ao banco no total, e não cento e uma.
/// </para>
/// </remarks>
public sealed class ConversationPresenter(
    DizidoDbContext db,
    IPresenceTracker presence,
    IObjectStorage storage)
{
    public async Task<List<ConversationResponse>> ApresentarAsync(
        IReadOnlyList<Conversation> conversas,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversas);

        if (conversas.Count == 0)
        {
            return [];
        }

        var idsDeUsuarios = conversas
            .SelectMany(c => c.Members.Select(m => m.UserId))
            .Distinct()
            .ToList();

        var nomes = await db.Profiles
            .AsNoTracking()
            .Where(u => idsDeUsuarios.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // O estado de presença precisa vir junto da lista de conversas.
        //
        // O evento PresenceChanged do SignalR só dispara quando alguém conecta ou desconecta.
        // Quem já estava online antes de você abrir o app nunca gerou evento nenhum para você —
        // então, sem isto, todo mundo aparece offline até se mexer. É o estado inicial que
        // faltava; o evento cuida das mudanças a partir daí.
        var online = (await presence.FilterOnlineAsync(idsDeUsuarios)).ToHashSet();

        var avatares = await AvataresAsync(conversas, ct);

        return [.. conversas.Select(c => new ConversationResponse(
            c.Id,
            c.Type.ToString(),
            c.Title,
            c.AvatarAttachmentId is { } avatar ? avatares.GetValueOrDefault(avatar) : null,
            c.CreatedAt,
            c.LastMessageAt,
            [.. c.Members
                .Where(m => m.IsActive)
                .Select(m => new ConversationMemberResponse(
                    m.UserId,
                    nomes.GetValueOrDefault(m.UserId, "(desconhecido)"),
                    m.Role.ToString(),
                    m.LastReadMessageId,
                    online.Contains(m.UserId),
                    m.MutedUntil))]))];
    }

    public async Task<ConversationResponse> ApresentarUmaAsync(
        Conversation conversa, CancellationToken ct = default) =>
        (await ApresentarAsync([conversa], ct))[0];

    /// <summary>
    /// Assina as URLs dos avatares, em uma consulta para todas as conversas do lote.
    /// </summary>
    /// <remarks>
    /// Usa a <b>miniatura</b>, não a imagem original. O avatar aparece com 38 px de lado; baixar
    /// a foto de 4 MB que alguém escolheu para o grupo, em toda linha da lista, seria desperdício
    /// puro. A original só existe para quem abrir a imagem, o que aqui nem é possível.
    /// </remarks>
    private async Task<Dictionary<Guid, string>> AvataresAsync(
        IReadOnlyList<Conversation> conversas, CancellationToken ct)
    {
        var ids = conversas
            .Where(c => c.AvatarAttachmentId is not null)
            .Select(c => c.AvatarAttachmentId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var anexos = await db.Attachments
            .AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(ct);

        return anexos.ToDictionary(
            a => a.Id,
            a => storage.CreateDownloadUrl(
                a.ThumbnailKey ?? a.StorageKey,
                a.FileName,
                a.ThumbnailKey is null ? a.ContentType : "image/jpeg",
                showInline: true).ToString());
    }
}
