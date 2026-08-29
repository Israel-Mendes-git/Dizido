using System.Net.Http.Json;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Contador de não lidas: por usuário, a partir da marca de leitura de cada um.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class NaoLidasTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task ContaAsMensagensQueChegaramDepoisDaMarca()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        await EnviarAsync(dono, grupo, "primeira");
        await EnviarAsync(dono, grupo, "segunda");
        await EnviarAsync(dono, grupo, "terceira");

        Assert.Equal(3, await NaoLidasDeAsync(membro, grupo));
    }

    /// <summary>
    /// A conta é de cada um. Quem enviou não tem nada a ler do que escreveu.
    /// </summary>
    [Fact]
    public async Task QuemEnviouNaoTemNaoLidas()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        await EnviarAsync(dono, grupo, "escrevi eu mesmo");

        Assert.Equal(0, await NaoLidasDeAsync(dono, grupo));
    }

    /// <summary>
    /// "Fulano entrou no grupo" não é algo a ler — se contasse, todo grupo novo já nasceria
    /// com notificação sem ninguém ter falado nada.
    /// </summary>
    [Fact]
    public async Task AvisoDoSistemaNaoConta()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        // Adicionar gera um aviso de sistema, e nada além disso.
        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        Assert.Equal(0, await NaoLidasDeAsync(Api.ClienteDe(membro), grupo.Id));
    }

    [Fact]
    public async Task MensagemApagadaDeixaDeContar()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var resposta = await EnviarAsync(dono, grupo, "vou me arrepender");
        var mensagem = (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;

        Assert.Equal(1, await NaoLidasDeAsync(membro, grupo));

        (await dono.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{mensagem.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        Assert.Equal(0, await NaoLidasDeAsync(membro, grupo));
    }

    /// <summary>
    /// A conta é por pessoa: o dono lê e o membro continua com as dele.
    /// </summary>
    [Fact]
    public async Task AContaDeUmNaoAfetaADoOutro()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();
        var terceiro = await Api.CriarUsuarioAsync("terceira");

        (await AdicionarAsync(dono, grupo, terceiro.Id)).EnsureSuccessStatusCode();

        await EnviarAsync(dono, grupo, "alô todo mundo");

        Assert.Equal(1, await NaoLidasDeAsync(membro, grupo));
        Assert.Equal(1, await NaoLidasDeAsync(Api.ClienteDe(terceiro), grupo));
        Assert.Equal(0, await NaoLidasDeAsync(dono, grupo));
    }

    [Fact]
    public async Task NaoContaConversaDaQualSaiu()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var clienteDoMembro = Api.ClienteDe(membro);

        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");
        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        await EnviarAsync(clienteDoDono, grupo.Id, "antes de sair");

        Assert.Equal(1, await NaoLidasDeAsync(clienteDoMembro, grupo.Id));

        (await RemoverAsync(clienteDoMembro, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        // A conversa some da lista dele, então não há contagem nenhuma para achar.
        Assert.Null(await ProcurarNaListaAsync(clienteDoMembro, grupo.Id));
    }

    // ----- auxiliares -----

    /// <summary>
    /// Lê a contagem da LISTA de conversas, que é onde a tela a usa.
    /// </summary>
    private static async Task<int> NaoLidasDeAsync(HttpClient cliente, Guid grupo) =>
        (await ProcurarNaListaAsync(cliente, grupo))?.UnreadCount
        ?? throw new InvalidOperationException("A conversa não apareceu na lista.");

    private static async Task<ConversationResponse?> ProcurarNaListaAsync(HttpClient cliente, Guid grupo)
    {
        var conversas = await cliente.GetFromJsonAsync<List<ConversationResponse>>(
            new Uri("/api/conversations", UriKind.Relative));

        return conversas?.FirstOrDefault(c => c.Id == grupo);
    }

    private async Task<(Guid Grupo, HttpClient Dono, HttpClient Membro)> GrupoComMembroAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        return (grupo.Id, clienteDoDono, Api.ClienteDe(membro));
    }
}
