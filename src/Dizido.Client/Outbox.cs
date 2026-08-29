using System.Text.Json;

namespace Dizido.Client;

/// <summary>Uma mensagem escrita pelo usuário que ainda não foi confirmada pelo servidor.</summary>
/// <param name="AttachmentId">
/// O anexo, quando a mensagem leva um. Só o identificador — os bytes já estão no storage
/// antes de a mensagem entrar aqui. Guardar o arquivo na fila seria inviável: o
/// <c>localStorage</c> tem cerca de 5 MB no total, e Base64 ainda infla o conteúdo em um terço.
/// <para>
/// A consequência é assumida: anexar exige estar online <b>naquele momento</b>. Depois que o
/// upload termina, a mensagem em si vira um item de fila comum e ganha toda a resiliência da
/// Fase 5 — reenvio, backoff, sobreviver a fechar o navegador.
/// </para>
/// </param>
/// <param name="ReplyToMessageId">
/// A mensagem que esta responde. Vai na fila para que uma resposta escrita offline continue
/// sendo uma resposta quando finalmente sair — sem isso, ela chegaria solta e fora de contexto
/// horas depois.
/// </param>
public sealed record ItemDaFila(
    Guid ConversationId,
    Guid ClientMessageId,
    string Body,
    DateTimeOffset CriadaEm,
    Guid? AttachmentId = null,
    Guid? ReplyToMessageId = null,

    /// <summary>Quem foi citado. Vai na fila para a menção sobreviver a um envio offline.</summary>
    IReadOnlyList<Guid>? Citados = null,

    int Tentativas = 0);

/// <summary>Onde a fila de saída é guardada. Abstraído para o Domain do cliente não conhecer o navegador.</summary>
public interface IArmazenamentoLocal
{
    Task<string?> LerAsync(string chave);

    Task GravarAsync(string chave, string valor);

    Task RemoverAsync(string chave);
}

/// <summary>
/// Fila de mensagens pendentes de envio, persistida no dispositivo.
/// </summary>
/// <remarks>
/// <para>
/// É o que separa "chat de demonstração" de "aplicativo de mensagens". Sem ela, escrever uma
/// mensagem com a rede oscilando significa perdê-la — e o usuário só descobre depois, quando
/// ninguém responde.
/// </para>
/// <para>
/// Persistir importa porque o cenário real é fechar o navegador (ou o celular matar a aba) antes
/// de a conexão voltar. Uma fila só em memória some junto.
/// </para>
/// </remarks>
public sealed class Outbox(IArmazenamentoLocal armazenamento, DizidoSession session)
{
    private const string Prefixo = "dizido:outbox:";

    private readonly List<ItemDaFila> _itens = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<ItemDaFila> Itens => _itens;

    public bool TemPendencias => _itens.Count > 0;

    // A chave inclui o usuário: num dispositivo compartilhado, a fila de um não pode
    // ser enviada pela conta do outro.
    private string Chave => $"{Prefixo}{session.UserId}";

    public async Task CarregarAsync()
    {
        _itens.Clear();

        var json = await armazenamento.LerAsync(Chave);

        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var salvos = JsonSerializer.Deserialize<List<ItemDaFila>>(json, Json);

            if (salvos is not null)
            {
                _itens.AddRange(salvos);
            }
        }
        catch (JsonException)
        {
            // Fila corrompida (versão antiga do formato, escrita interrompida). Descartar é
            // melhor do que travar o app na inicialização: perdemos mensagens não enviadas,
            // mas o usuário consegue usar o aplicativo.
            await armazenamento.RemoverAsync(Chave);
        }
    }

    public async Task EnfileirarAsync(ItemDaFila item)
    {
        _itens.Add(item);
        await PersistirAsync();
    }

    public async Task RemoverAsync(Guid clientMessageId)
    {
        _itens.RemoveAll(i => i.ClientMessageId == clientMessageId);
        await PersistirAsync();
    }

    public async Task RegistrarTentativaAsync(Guid clientMessageId)
    {
        var indice = _itens.FindIndex(i => i.ClientMessageId == clientMessageId);

        if (indice >= 0)
        {
            _itens[indice] = _itens[indice] with { Tentativas = _itens[indice].Tentativas + 1 };
            await PersistirAsync();
        }
    }

    public async Task LimparAsync()
    {
        _itens.Clear();
        await armazenamento.RemoverAsync(Chave);
    }

    private Task PersistirAsync() =>
        _itens.Count == 0
            ? armazenamento.RemoverAsync(Chave)
            : armazenamento.GravarAsync(Chave, JsonSerializer.Serialize(_itens, Json));
}
