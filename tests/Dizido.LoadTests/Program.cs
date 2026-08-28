using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dizido.Api.Auth;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;
using Dizido.Domain.Entities;
using Dizido.Infrastructure;
using Dizido.Infrastructure.Identity;
using Dizido.Infrastructure.Persistence;
using Dizido.LoadTests;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// ---------------------------------------------------------------------------
// Teste de carga do Dizido: quantos WebSockets simultâneos antes de degradar?
// ---------------------------------------------------------------------------
//
// Escrito à mão, e não com um framework de carga, por duas razões:
//
//   1. O NBomber pede licença comercial, e o k6 exigiria instalar um binário externo e
//      reescrever o protocolo do SignalR em JavaScript.
//   2. A pergunta aqui é específica — latência de ENTREGA em tempo real, medida do POST de
//      quem enviou até o evento chegar em quem recebe. Nenhum gerador de carga genérico mede
//      isso de graça; é preciso instrumentar os dois lados de qualquer jeito.
//
// Como rodar:
//
//   dotnet run --project tests/Dizido.LoadTests -- --conexoes 200 --duracao 30
//
// Contra um ambiente de teste, nunca contra produção: o programa cria usuários no banco.

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Dizido"] = "Host=localhost;Port=5432;Database=dizido;Username=dizido;Password=dizido_dev",
        ["Jwt:Issuer"] = "dizido-dev",
        ["Jwt:Audience"] = "dizido-dev",
        ["Jwt:SigningKey"] = "chave-de-desenvolvimento-do-dizido-nao-use-em-producao-1234567890",
        ["Jwt:AccessTokenLifetime"] = "01:00:00",
    })
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var api = config["api"] ?? "http://localhost:5224";
var conexoesAlvo = int.Parse(config["conexoes"] ?? "100", System.Globalization.CultureInfo.InvariantCulture);
var duracao = TimeSpan.FromSeconds(int.Parse(config["duracao"] ?? "20", System.Globalization.CultureInfo.InvariantCulture));

// Quantos dos participantes efetivamente enviam. Numa conversa real a maioria só lê; simular
// todo mundo escrevendo ao mesmo tempo mediria um cenário que não existe.
var remetentes = Math.Max(1, conexoesAlvo / 10);

Console.WriteLine($"API: {api}");
Console.WriteLine($"Conexões: {conexoesAlvo} | remetentes: {remetentes} | duração: {duracao.TotalSeconds:0}s");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Preparação: usuários e um grupo em comum
// ---------------------------------------------------------------------------

var servicos = new ServiceCollection();
servicos.AddLogging();
servicos.AddDizidoInfrastructure(config);
servicos.AddSingleton(TimeProvider.System);
servicos.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
servicos.AddSingleton<IAccessTokenService, AccessTokenService>();
servicos.AddIdentityCore<DizidoUser>(PoliticaDeSenha.Aplicar).AddEntityFrameworkStores<DizidoDbContext>();

using var provider = servicos.BuildServiceProvider();
using var escopo = provider.CreateScope();

var db = escopo.ServiceProvider.GetRequiredService<DizidoDbContext>();
var contas = escopo.ServiceProvider.GetRequiredService<UserManager<DizidoUser>>();
var tokens = escopo.ServiceProvider.GetRequiredService<IAccessTokenService>();
var jwt = escopo.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;

Console.Write("semeando usuários... ");

var agora = DateTimeOffset.UtcNow;
var marca = agora.ToUnixTimeMilliseconds();
var participantes = new List<(Guid Id, string Token)>();

for (var i = 0; i < conexoesAlvo; i++)
{
    var id = Guid.CreateVersion7(agora);
    var email = $"carga-{marca}-{i}@dizido.teste";

    var conta = new DizidoUser { Id = id, UserName = email, Email = email, CreatedAt = agora };
    var resultado = await contas.CreateAsync(conta, "carga-dizido-2026");

    if (!resultado.Succeeded)
    {
        Console.WriteLine($"falhou: {string.Join("; ", resultado.Errors.Select(e => e.Description))}");
        return 1;
    }

    db.Profiles.Add(UserProfile.Create(id, $"Carga {i}", agora));
    participantes.Add((id, tokens.Create(conta, agora)));
}

// Um grupo com todos dentro. É o pior caso realista para o tempo real: cada mensagem enviada
// precisa alcançar todas as conexões, então o custo de entrega cresce com o tamanho do grupo.
var grupo = Conversation.CreateGroup($"Carga {marca}", participantes[0].Id, agora);

foreach (var (id, _) in participantes.Skip(1))
{
    grupo.AddMember(participantes[0].Id, id, agora);
}

db.Conversations.Add(grupo);
await db.SaveChangesAsync();

Console.WriteLine($"{participantes.Count} usuários, grupo {grupo.Id}");

// ---------------------------------------------------------------------------
// Abrir as conexões
// ---------------------------------------------------------------------------

var recebimentos = new ConcurrentBag<double>();
var handshakes = new ConcurrentBag<double>();
var falhasDeConexao = 0;

// O relógio de referência das mensagens em voo.
//
// A chave é o ClientMessageId, e não o Id que o servidor atribui — por uma corrida real: o
// servidor emite o evento do SignalR ANTES de responder o POST, então a entrega chega ao
// cliente antes de ele saber o Id da mensagem. Indexando pelo Id do servidor, toda entrega
// mais rápida que a resposta HTTP era descartada da medição, e o resultado dizia "83% de
// entrega" com latência p50 de 0 ms — as duas coisas erradas, e nenhuma delas parecendo erro.
//
// O ClientMessageId é gerado antes do envio, então a marcação de tempo pode ser feita antes
// de qualquer coisa sair. E o que se mede passa a ser o que o usuário sente: do momento em
// que ele aperta enviar até a mensagem aparecer na tela do outro.
var enviadas = new ConcurrentDictionary<Guid, long>();

Console.Write("abrindo conexões... ");
var cronometroDeAbertura = Stopwatch.StartNew();

var conexoes = new List<HubConnection>();

await Parallel.ForEachAsync(participantes, async (participante, ct) =>
{
    var conexao = new HubConnectionBuilder()
        .WithUrl($"{api}/hubs/chat", opcoes =>
            opcoes.AccessTokenProvider = () => Task.FromResult<string?>(participante.Token))
        .Build();

    // O evento chega em todas as conexões do grupo. Cada uma registra quanto tempo passou
    // desde que o envio começou — é essa a latência que o usuário sente.
    conexao.On<MessageResponse>("MessageReceived", mensagem =>
    {
        if (enviadas.TryGetValue(mensagem.ClientMessageId, out var quandoEnviou))
        {
            recebimentos.Add(Stopwatch.GetElapsedTime(quandoEnviou).TotalMilliseconds);
        }
    });

    var cronometro = Stopwatch.StartNew();

    try
    {
        await conexao.StartAsync(ct);
        handshakes.Add(cronometro.Elapsed.TotalMilliseconds);

        lock (conexoes)
        {
            conexoes.Add(conexao);
        }
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        Interlocked.Increment(ref falhasDeConexao);
        await conexao.DisposeAsync();
    }
});

Console.WriteLine($"{conexoes.Count} abertas em {cronometroDeAbertura.Elapsed.TotalSeconds:0.0}s"
                  + (falhasDeConexao > 0 ? $" ({falhasDeConexao} falharam)" : string.Empty));

if (conexoes.Count == 0)
{
    Console.WriteLine("Nenhuma conexão abriu. A API está no ar?");
    return 1;
}

// ---------------------------------------------------------------------------
// Enviar durante a janela combinada
// ---------------------------------------------------------------------------

Console.WriteLine($"enviando por {duracao.TotalSeconds:0}s...");

var fim = DateTimeOffset.UtcNow + duracao;
var enviosComErro = 0;
var totalEnviado = 0;

await Parallel.ForEachAsync(participantes.Take(remetentes), async (remetente, ct) =>
{
    using var http = new HttpClient { BaseAddress = new Uri(api) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", remetente.Token);

    while (DateTimeOffset.UtcNow < fim && !ct.IsCancellationRequested)
    {
        var clientMessageId = Guid.NewGuid();

        // Marcado ANTES de a requisição sair: o servidor emite o evento do SignalR antes de
        // responder o POST, e uma entrega mais rápida que a resposta HTTP não pode escapar
        // da conta.
        enviadas[clientMessageId] = Stopwatch.GetTimestamp();

        try
        {
            var resposta = await http.PostAsJsonAsync(
                $"api/conversations/{grupo.Id}/messages",
                new SendMessageRequest(clientMessageId, "carga"),
                ct);

            if (resposta.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref totalEnviado);
            }
            else
            {
                enviadas.TryRemove(clientMessageId, out _);
                Interlocked.Increment(ref enviosComErro);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            enviadas.TryRemove(clientMessageId, out _);
            Interlocked.Increment(ref enviosComErro);
        }

        // Uma mensagem por segundo por remetente. Sem a pausa isto vira um teste de quanto o
        // gerador de carga aguenta, não de quanto o servidor aguenta.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }
});

// Sobra para as últimas mensagens chegarem antes de fechar tudo.
await Task.Delay(TimeSpan.FromSeconds(3));

// ---------------------------------------------------------------------------
// Resultado
// ---------------------------------------------------------------------------

var esperados = totalEnviado * conexoes.Count;
var chegaram = recebimentos.Count;

Console.WriteLine();
Console.WriteLine("--- resultado ---------------------------------------------");
Console.WriteLine($"conexões abertas    {conexoes.Count} de {conexoesAlvo}"
                  + (falhasDeConexao > 0 ? $"  ({falhasDeConexao} falharam)" : string.Empty));
Console.WriteLine($"handshake           {Percentis.De([.. handshakes])}");
Console.WriteLine($"mensagens enviadas  {totalEnviado}" + (enviosComErro > 0 ? $"  ({enviosComErro} com erro)" : string.Empty));
Console.WriteLine($"entregas esperadas  {esperados}");
Console.WriteLine($"entregas medidas    {chegaram}" + (esperados > 0 ? $"  ({100.0 * chegaram / esperados:0.0}%)" : string.Empty));
Console.WriteLine($"latência de entrega {Percentis.De([.. recebimentos])}");
Console.WriteLine("-----------------------------------------------------------");
Console.WriteLine();
Console.WriteLine("Entregas abaixo de 100% não são necessariamente perda: mensagens enviadas");
Console.WriteLine("no fim da janela podem não ter tido tempo de chegar. O que denuncia problema");
Console.WriteLine("é a porcentagem CAIR conforme o número de conexões sobe.");

foreach (var conexao in conexoes)
{
    await conexao.DisposeAsync();
}

return 0;
