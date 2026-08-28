using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dizido.Api.Tests;

/// <summary>
/// Os dois endpoints de saúde e a diferença entre eles.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class SaudeTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task LivenessRespondeSemTokenESemTocarNasDependencias()
    {
        var resposta = await Api.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        // O corpo é a palavra "Healthy" e nada mais: quem pergunta é o orquestrador, e a única
        // informação que ele precisa é o código de status.
        Assert.Equal("Healthy", (await resposta.Content.ReadAsStringAsync()).Trim());
    }

    /// <summary>
    /// Com as três dependências no ar (Postgres, Redis e MinIO em contêiner), o readiness
    /// tem que passar — e dizer o que conferiu.
    /// </summary>
    [Fact]
    public async Task ReadinessConfereAsTresDependencias()
    {
        var resposta = await Api.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var relatorio = await resposta.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Healthy", relatorio.GetProperty("estado").GetString());

        var checagens = relatorio.GetProperty("checagens");

        foreach (var nome in new[] { "postgres", "redis", "storage" })
        {
            Assert.True(
                checagens.TryGetProperty(nome, out var checagem),
                $"O readiness deveria conferir '{nome}', e não confere.");

            Assert.Equal("Healthy", checagem.GetProperty("estado").GetString());
        }
    }

    /// <summary>
    /// O readiness é anônimo por necessidade — o orquestrador não tem como se autenticar —,
    /// e por isso não pode devolver stack trace.
    /// </summary>
    [Fact]
    public async Task ReadinessNaoVazaDetalheInterno()
    {
        var corpo = await Api.CreateClient().GetStringAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.DoesNotContain("Exception", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", corpo, StringComparison.OrdinalIgnoreCase);
    }
}
