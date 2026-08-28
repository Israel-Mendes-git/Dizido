using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Conversations;

namespace Dizido.Api.Tests;

/// <summary>
/// A hierarquia de cargos vista pelo HTTP: dono &gt; administrador &gt; membro.
/// </summary>
/// <remarks>
/// As regras em si já são testadas sem banco em <c>Dizido.Domain.Tests</c>, e em milissegundos.
/// O que estes testes acrescentam é outra coisa: que o endpoint <b>chega</b> à regra. Um handler
/// que esquecesse de chamar o método da entidade, ou que carregasse a conversa sem os membros,
/// passaria nos testes de domínio e abriria o grupo para qualquer um.
/// </remarks>
[Collection(ColecaoDaApi.Nome)]
public sealed class AutorizacaoDeGrupoTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task MembroComumNaoRenomeiaMasOAdministradorSim()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        await RecusadoPorRegraAsync(
            await RenomearAsync(Api.ClienteDe(membro), grupo, "Renomeado por quem não pode"),
            "administrador");

        (await RenomearAsync(Api.ClienteDe(dono), grupo, "Renomeado pela dona")).EnsureSuccessStatusCode();

        var depois = await VerConversaAsync(Api.ClienteDe(membro), grupo);
        Assert.Equal("Renomeado pela dona", depois.Title);
    }

    [Fact]
    public async Task MembroComumNaoAdicionaNemRemoveNinguem()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();
        var forasteiro = await Api.CriarUsuarioAsync("forasteira");

        var cliente = Api.ClienteDe(membro);

        await RecusadoPorRegraAsync(await AdicionarAsync(cliente, grupo, forasteiro.Id), "administrador");
        await RecusadoPorRegraAsync(await RemoverAsync(cliente, grupo, dono.Id), "administrador");
    }

    [Fact]
    public async Task MembroComumNaoAlteraCargos()
    {
        var (grupo, _, membro) = await GrupoComMembroAsync();

        await RecusadoPorRegraAsync(
            await DefinirCargoAsync(Api.ClienteDe(membro), grupo, membro.Id, "Admin"),
            "dono");
    }

    /// <summary>
    /// Dois administradores não podem se expulsar. Sem esta regra, o grupo ficaria à mercê
    /// de quem clicasse primeiro.
    /// </summary>
    [Fact]
    public async Task AdministradorNaoRemoveOutroAdministradorNemODono()
    {
        var (grupo, dono, primeiro) = await GrupoComMembroAsync();
        var segundo = await Api.CriarUsuarioAsync("segunda");

        var clienteDoDono = Api.ClienteDe(dono);

        (await AdicionarAsync(clienteDoDono, grupo, segundo.Id)).EnsureSuccessStatusCode();
        (await DefinirCargoAsync(clienteDoDono, grupo, primeiro.Id, "Admin")).EnsureSuccessStatusCode();
        (await DefinirCargoAsync(clienteDoDono, grupo, segundo.Id, "Admin")).EnsureSuccessStatusCode();

        var clienteDoPrimeiro = Api.ClienteDe(primeiro);

        await RecusadoPorRegraAsync(await RemoverAsync(clienteDoPrimeiro, grupo, segundo.Id), "cargo inferior");
        await RecusadoPorRegraAsync(await RemoverAsync(clienteDoPrimeiro, grupo, dono.Id), "cargo inferior");
    }

    [Fact]
    public async Task AdministradorRemoveMembroComum()
    {
        var (grupo, dono, admin) = await GrupoComMembroAsync();
        var comum = await Api.CriarUsuarioAsync("comum");

        var clienteDoDono = Api.ClienteDe(dono);

        (await AdicionarAsync(clienteDoDono, grupo, comum.Id)).EnsureSuccessStatusCode();
        (await DefinirCargoAsync(clienteDoDono, grupo, admin.Id, "Admin")).EnsureSuccessStatusCode();

        (await RemoverAsync(Api.ClienteDe(admin), grupo, comum.Id)).EnsureSuccessStatusCode();

        var depois = await VerConversaAsync(clienteDoDono, grupo);
        Assert.DoesNotContain(depois.Members, m => m.UserId == comum.Id);
    }

    [Fact]
    public async Task NemOAdministradorRebaixaODono()
    {
        var (grupo, dono, admin) = await GrupoComMembroAsync();

        (await DefinirCargoAsync(Api.ClienteDe(dono), grupo, admin.Id, "Admin")).EnsureSuccessStatusCode();

        await RecusadoPorRegraAsync(
            await DefinirCargoAsync(Api.ClienteDe(admin), grupo, dono.Id, "Member"),
            "dono");
    }

    [Fact]
    public async Task DonoNaoSaiSemTransferirOGrupo()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var clienteDoDono = Api.ClienteDe(dono);

        await RecusadoPorRegraAsync(await RemoverAsync(clienteDoDono, grupo, dono.Id), "transferir");

        (await TransferirAsync(clienteDoDono, grupo, membro.Id)).EnsureSuccessStatusCode();
        (await RemoverAsync(clienteDoDono, grupo, dono.Id)).EnsureSuccessStatusCode();

        // Depois da transferência, quem era membro comum manda no grupo.
        (await RenomearAsync(Api.ClienteDe(membro), grupo, "Agora é meu")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SoODonoTransfereOGrupo()
    {
        var (grupo, _, membro) = await GrupoComMembroAsync();

        await RecusadoPorRegraAsync(
            await TransferirAsync(Api.ClienteDe(membro), grupo, membro.Id),
            "dono");
    }

    /// <summary>
    /// Cargo desconhecido é 400 com explicação, não 500 nem um <c>Enum.Parse</c> estourando.
    /// </summary>
    [Fact]
    public async Task CargoDesconhecidoEhRecusadoComExplicacao()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var resposta = await DefinirCargoAsync(Api.ClienteDe(dono), grupo, membro.Id, "Imperador");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaHttp>();
        Assert.Contains("Imperador", problema?.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Promover a Owner por este caminho é recusado: quem manda no dono é a transferência,
    /// que rebaixa o anterior no mesmo passo. Sem a recusa, o grupo ficaria com dois donos.
    /// </summary>
    [Fact]
    public async Task NaoSePromoveAlguemADonoPelaRotaDeCargo()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        await RecusadoPorRegraAsync(
            await DefinirCargoAsync(Api.ClienteDe(dono), grupo, membro.Id, "Owner"),
            "transfer");
    }

    /// <summary>Cria um grupo com dono e um membro comum já dentro.</summary>
    private async Task<(Guid Grupo, DizidoApiFactory.Usuario Dono, DizidoApiFactory.Usuario Membro)>
        GrupoComMembroAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        return (grupo.Id, dono, membro);
    }

    private static async Task<ConversationResponse> VerConversaAsync(HttpClient cliente, Guid grupo)
    {
        var resposta = await VerAsync(cliente, grupo);

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }
}
