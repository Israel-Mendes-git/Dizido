using System.Diagnostics.Metrics;

namespace Dizido.Api.Observabilidade;

/// <summary>
/// As métricas próprias do Dizido — as que respondem perguntas sobre o produto, e não sobre
/// o processo.
/// </summary>
/// <remarks>
/// <para>
/// A instrumentação automática do ASP.NET já conta requisições, latência e códigos de status.
/// O que ela não sabe é quantas mensagens foram enviadas, quantos WebSockets estão abertos ou
/// quanto os anexos estão consumindo — e são essas que dizem se o produto está sendo usado.
/// </para>
/// <para>
/// Note que a classe usa <see cref="Meter"/>, do próprio .NET, e não a API do OpenTelemetry.
/// A diferença importa: o código que mede não conhece quem coleta. Trocar o OpenTelemetry por
/// outra coisa um dia mexe só no registro, e nenhum endpoint precisa mudar.
/// </para>
/// <para>
/// <b>Nada aqui identifica uma pessoa.</b> Métrica é agregada e vai para um sistema com regras
/// de acesso mais frouxas que o banco. Um rótulo com id de usuário viraria, na prática, um
/// registro de quem falou com quem — e ainda estouraria a cardinalidade, que é o jeito mais
/// comum de derrubar um backend de métricas.
/// </para>
/// </remarks>
public sealed class DizidoMetrics : IDisposable
{
    /// <summary>O nome que o coletor precisa conhecer para receber estas métricas.</summary>
    public const string Nome = "Dizido";

    private readonly Meter _meter;
    private readonly Counter<long> _mensagens;
    private readonly Counter<long> _anexos;
    private readonly Histogram<long> _tamanhoDeAnexo;
    private readonly UpDownCounter<long> _conexoes;

    public DizidoMetrics(IMeterFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _meter = factory.Create(Nome);

        _mensagens = _meter.CreateCounter<long>(
            "dizido.mensagens.enviadas", unit: "{mensagem}",
            description: "Mensagens gravadas com sucesso.");

        _anexos = _meter.CreateCounter<long>(
            "dizido.anexos.confirmados", unit: "{anexo}",
            description: "Anexos que passaram na conferência de conteúdo.");

        _tamanhoDeAnexo = _meter.CreateHistogram<long>(
            "dizido.anexos.tamanho", unit: "By",
            description: "Tamanho dos anexos confirmados.");

        // UpDownCounter, e não Counter: conexão abre e fecha. Um contador comum só sobe, e
        // o gráfico mostraria o total histórico em vez de quantas estão abertas agora — que
        // é a pergunta que interessa quando o servidor começa a sofrer.
        _conexoes = _meter.CreateUpDownCounter<long>(
            "dizido.conexoes.ativas", unit: "{conexão}",
            description: "Conexões de tempo real abertas neste processo.");
    }

    /// <param name="tipo">"Text" ou "System" — poucos valores possíveis, cardinalidade segura.</param>
    public void MensagemEnviada(string tipo) =>
        _mensagens.Add(1, new KeyValuePair<string, object?>("tipo", tipo));

    public void AnexoConfirmado(string especie, long bytes)
    {
        var rotulo = new KeyValuePair<string, object?>("especie", especie);

        _anexos.Add(1, rotulo);
        _tamanhoDeAnexo.Record(bytes, rotulo);
    }

    public void ConexaoAberta() => _conexoes.Add(1);

    public void ConexaoFechada() => _conexoes.Add(-1);

    public void Dispose() => _meter.Dispose();
}
