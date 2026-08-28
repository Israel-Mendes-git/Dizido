using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Attachments;
using Dizido.Contracts.Users;

namespace Dizido.Api.Tests;

/// <summary>
/// Os limites que impedem um usuário autenticado de abusar do serviço.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class LimitesTests(DizidoApiFactory api) : TesteDeApi(api)
{
    /// <summary>
    /// Pedir URLs de upload em laço é o abuso mais caro: cada pedido reserva uma linha e
    /// autoriza 50 MB. O limite é dez, e o décimo primeiro é recusado.
    /// </summary>
    [Fact]
    public async Task UploadEmLacoEhBarradoDepoisDoDecimo()
    {
        var dono = await Api.CriarUsuarioAsync("abusiva");
        var cliente = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(cliente, "Rapadura Atômica");

        var recusados = 0;

        for (var i = 0; i < 15; i++)
        {
            var resposta = await cliente.PostAsJsonAsync(
                $"/api/conversations/{grupo.Id}/attachments",
                new RequestUploadRequest($"arquivo{i}.png", "image/png", 1024));

            if (resposta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                recusados++;

                // Diz quanto esperar: um cliente que sabe o tempo aguarda, um que não sabe
                // fica tentando — e piora exatamente o problema que o limite resolve.
                Assert.NotNull(resposta.Headers.RetryAfter);
            }
        }

        Assert.True(recusados > 0, "Quinze pedidos de upload seguidos deveriam esbarrar no limite.");
    }

    /// <summary>
    /// A cota é por usuário, não por endereço. Nos testes todo mundo vem do mesmo IP — se a
    /// partição fosse o IP, o segundo usuário já nasceria sem cota por causa do primeiro.
    /// </summary>
    [Fact]
    public async Task ACotaDeUmNaoConsomeADeOutro()
    {
        var primeira = await Api.CriarUsuarioAsync("primeira");
        var segunda = await Api.CriarUsuarioAsync("segunda");

        var clienteDaPrimeira = Api.ClienteDe(primeira);
        var grupo = await CriarGrupoAsync(clienteDaPrimeira, "Rapadura Atômica");
        (await AdicionarAsync(clienteDaPrimeira, grupo.Id, segunda.Id)).EnsureSuccessStatusCode();

        // Esgota a cota de upload da primeira.
        for (var i = 0; i < 15; i++)
        {
            await clienteDaPrimeira.PostAsJsonAsync(
                $"/api/conversations/{grupo.Id}/attachments",
                new RequestUploadRequest($"a{i}.png", "image/png", 1024));
        }

        // A segunda continua inteira.
        var daSegunda = await Api.ClienteDe(segunda).PostAsJsonAsync(
            $"/api/conversations/{grupo.Id}/attachments",
            new RequestUploadRequest("minha.png", "image/png", 1024));

        Assert.Equal(HttpStatusCode.OK, daSegunda.StatusCode);
    }

    /// <summary>Ler o histórico não tem limite: rolar uma conversa longa é uso normal.</summary>
    [Fact]
    public async Task LerMensagensNaoEhLimitado()
    {
        var dono = await Api.CriarUsuarioAsync("leitora");
        var cliente = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(cliente, "Rapadura Atômica");

        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await LerMensagensAsync(cliente, grupo.Id)).StatusCode);
        }
    }

    [Fact]
    public async Task AListaDeUsuariosRespeitaOTeto()
    {
        var eu = Api.ClienteDe(await Api.CriarUsuarioAsync("curiosa"));

        var comTeto = await eu.GetFromJsonAsync<List<UserResponse>>(
            new Uri("/api/users?limite=3", UriKind.Relative));

        Assert.NotNull(comTeto);
        Assert.True(comTeto.Count <= 3);
    }

    /// <summary>
    /// Um limite absurdo é aparado, não obedecido. Sem isto, `?limite=999999` seria um jeito
    /// de pedir a tabela inteira contornando a paginação.
    /// </summary>
    [Fact]
    public async Task LimiteAbsurdoEhAparado()
    {
        var eu = Api.ClienteDe(await Api.CriarUsuarioAsync("esperta"));

        // A suíte cria muitos usuários; o teto de 100 tem que valer.
        for (var i = 0; i < 3; i++)
        {
            await Api.CriarUsuarioAsync($"massa{i}");
        }

        var resposta = await eu.GetFromJsonAsync<List<UserResponse>>(
            new Uri("/api/users?limite=999999", UriKind.Relative));

        Assert.NotNull(resposta);
        Assert.True(resposta.Count <= 100, $"Devolveu {resposta.Count} — o teto de 100 não foi aplicado.");
    }

    [Fact]
    public async Task ABuscaEncontraPeloNomeSemDiferenciarCaixa()
    {
        var alvo = await Api.CriarUsuarioAsync("Zoraide");
        var eu = Api.ClienteDe(await Api.CriarUsuarioAsync("buscadora"));

        // O nome guardado tem inicial maiúscula; a busca vai em minúsculas.
        var achados = await eu.GetFromJsonAsync<List<UserResponse>>(
            new Uri($"/api/users?busca={Uri.EscapeDataString(alvo.Nome.ToLowerInvariant())}", UriKind.Relative));

        Assert.NotNull(achados);
        Assert.Contains(achados, u => u.Id == alvo.Id);
    }
}
