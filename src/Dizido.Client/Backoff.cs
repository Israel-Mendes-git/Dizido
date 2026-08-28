namespace Dizido.Client;

/// <summary>Calcula quanto esperar antes da próxima tentativa.</summary>
public static class Backoff
{
    private static readonly TimeSpan Teto = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Espera exponencial com aleatoriedade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parte exponencial é óbvia: se falhou, insistir imediatamente só desperdiça bateria e
    /// piora a situação de um servidor que já está com problema.
    /// </para>
    /// <para>
    /// A aleatoriedade (<i>jitter</i>) é o detalhe que quase todo mundo esquece. Se o servidor
    /// cair, TODOS os clientes falham no mesmo instante — e sem jitter todos calculariam
    /// exatamente o mesmo intervalo e voltariam juntos, em ondas sincronizadas, derrubando o
    /// servidor de novo assim que ele levantasse. É o problema da "trovoada de rebanho".
    /// Espalhar as tentativas resolve.
    /// </para>
    /// <para>
    /// Usamos <i>full jitter</i>: um valor aleatório entre zero e o intervalo calculado, em vez
    /// de o intervalo mais um ruído pequeno. Espalha muito melhor.
    /// </para>
    /// </remarks>
    public static TimeSpan Calcular(int tentativa, Random aleatorio)
    {
        ArgumentNullException.ThrowIfNull(aleatorio);

        var expoente = Math.Min(tentativa, 10); // 2^10 s ≈ 17 min, já acima do teto
        var baseSegundos = Math.Min(Math.Pow(2, expoente), Teto.TotalSeconds);

        return TimeSpan.FromSeconds(aleatorio.NextDouble() * baseSegundos);
    }
}
