namespace Dizido.LoadTests;

/// <summary>
/// Resume uma lista de medições em percentis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Média não serve para latência.</b> Numa amostra em que 99 requisições levam 10 ms e uma
/// leva 5 segundos, a média dá 60 ms — um número que descreve exatamente ninguém. O p99 dá
/// 5 segundos, que é o que aquele usuário viveu.
/// </para>
/// <para>
/// Num app de mensagens isso importa mais do que no comum: quem mandou a mensagem que demorou
/// cinco segundos é quem vai reclamar, e a média esconde justamente essa pessoa.
/// </para>
/// </remarks>
internal sealed record Percentis(int Amostras, double P50, double P95, double P99, double Maximo)
{
    public static Percentis De(IReadOnlyList<double> valores)
    {
        if (valores.Count == 0)
        {
            return new Percentis(0, 0, 0, 0, 0);
        }

        var ordenados = valores.Order().ToArray();

        return new Percentis(
            ordenados.Length,
            Em(ordenados, 0.50),
            Em(ordenados, 0.95),
            Em(ordenados, 0.99),
            ordenados[^1]);
    }

    private static double Em(double[] ordenados, double fracao)
    {
        var indice = (int)Math.Ceiling(fracao * ordenados.Length) - 1;

        return ordenados[Math.Clamp(indice, 0, ordenados.Length - 1)];
    }

    public override string ToString() =>
        $"n={Amostras}  p50={P50:0} ms  p95={P95:0} ms  p99={P99:0} ms  máx={Maximo:0} ms";
}
