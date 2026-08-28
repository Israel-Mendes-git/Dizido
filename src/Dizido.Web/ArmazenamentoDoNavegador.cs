using Dizido.Client;
using Microsoft.JSInterop;

namespace Dizido.Web;

/// <summary>
/// Guarda a fila de saída no <c>localStorage</c> do navegador.
/// </summary>
/// <remarks>
/// <para>
/// Aqui o <c>localStorage</c> é apropriado, ao contrário do que vale para o token de acesso.
/// A diferença é o que se perde num XSS: o token dá acesso à conta inteira; a fila contém
/// mensagens que o próprio usuário acabou de escrever e que já vão para o servidor de qualquer
/// forma. Não há segredo novo exposto.
/// </para>
/// <para>
/// IndexedDB seria o certo se a fila pudesse ficar grande (com anexos, por exemplo) — o
/// localStorage tem limite de ~5 MB e é síncrono, travando a interface em gravações grandes.
/// Para mensagens de texto pendentes, é folgado. Revisitar na Fase 7.
/// </para>
/// </remarks>
internal sealed class ArmazenamentoDoNavegador(IJSRuntime js) : IArmazenamentoLocal
{
    public async Task<string?> LerAsync(string chave)
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", chave);
        }
        catch (JSException)
        {
            // Navegação privada com armazenamento bloqueado, ou cota estourada.
            // O app continua funcionando — só perde a persistência da fila entre sessões.
            return null;
        }
    }

    public async Task GravarAsync(string chave, string valor)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", chave, valor);
        }
        catch (JSException)
        {
            // Idem: falhar em persistir não pode impedir o envio da mensagem.
        }
    }

    public async Task RemoverAsync(string chave)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", chave);
        }
        catch (JSException)
        {
        }
    }
}
