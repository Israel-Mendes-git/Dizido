using System.Net.Http.Headers;
using Dizido.Api.Auth;
using Dizido.Api.Realtime;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Identity;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Dizido.Api.Tests;

/// <summary>
/// Sobe a API inteira em memória, ligada a um PostgreSQL de verdade rodando em contêiner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que um banco real e não um falso.</b> Trocar o provedor por InMemory ou SQLite testaria
/// outro banco: a paginação por cursor usa SQL cru, o índice único de deduplicação é do Postgres,
/// e <c>DateTimeOffset</c> tem tradução própria no Npgsql. Um teste que passa contra um provedor
/// diferente do de produção dá a sensação de cobertura sem a garantia.
/// </para>
/// <para>
/// O contêiner é descartável e ganha porta aleatória, então não conflita com o Postgres do
/// <c>docker-compose.yml</c> nem deixa sujeira entre execuções. A imagem é a mesma do compose
/// de propósito — testar contra outra versão do banco enfraquece o teste.
/// </para>
/// <para>
/// <b>O que é substituído.</b> Só o que exigiria Redis: a presença e o backplane do SignalR.
/// Tudo o mais é o código de produção — o mesmo <c>Program.cs</c>, os mesmos endpoints, o mesmo
/// pipeline de autenticação.
/// </para>
/// </remarks>
public sealed class DizidoApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // A mesma imagem do docker-compose.yml: testar contra outra versão do banco enfraquece o teste.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    /// <summary>Um usuário de teste já criado no banco, com um token de acesso válido.</summary>
    public sealed record Usuario(Guid Id, string Nome, string Token);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Fica em Development porque é lá que appsettings.Development.json define a seção Jwt,
        // sem a qual o Program.cs falha ao subir. Só as connection strings são trocadas.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Dizido"] = _postgres.GetConnectionString(),

                // O Program.cs exige a chave presente, mas nada aqui conecta ao Redis:
                // o IConnectionMultiplexer é removido logo abaixo.
                ["ConnectionStrings:Redis"] = "localhost:6379",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPresenceTracker>();
            services.AddSingleton<IPresenceTracker, PresencaEmMemoria>();

            // O SignalR passa a usar o gerenciador em memória, o padrão de quando não há
            // backplane. Sem esta troca, o primeiro endpoint que resolvesse IHubContext
            // tentaria abrir conexão com o Redis e o teste travaria até estourar o tempo.
            services.AddSingleton(typeof(HubLifetimeManager<>), typeof(DefaultHubLifetimeManager<>));

            // Removido, e não substituído por um falso, de propósito: se algum código novo
            // passar a depender do Redis, o teste falha dizendo exatamente isso, em vez de
            // ficar pendurado tentando conectar.
            services.RemoveAll<StackExchange.Redis.IConnectionMultiplexer>();
        });
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Migrations, e não EnsureCreated: assim o esquema testado é exatamente o que o deploy
        // vai aplicar. EnsureCreated monta as tabelas a partir do modelo e passaria por cima de
        // uma migration quebrada — justamente o erro que mais interessa pegar aqui.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DizidoDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Cria uma conta com perfil e devolve um token de acesso válido para ela.
    /// </summary>
    /// <remarks>
    /// Não passa por <c>POST /api/auth/register</c> de propósito. Esse endpoint tem limite de
    /// 10 requisições por minuto por IP, e nos testes todas saem do mesmo IP — a partir do
    /// décimo primeiro usuário criado a suíte começaria a receber 429 e a falhar por um motivo
    /// que não tem nada a ver com o que se quer verificar. Aqui usamos as mesmas peças que o
    /// endpoint usa (UserManager e IAccessTokenService), então a conta é idêntica à real.
    /// </remarks>
    public async Task<Usuario> CriarUsuarioAsync(string nome)
    {
        using var scope = Services.CreateScope();
        var provider = scope.ServiceProvider;

        var contas = provider.GetRequiredService<UserManager<DizidoUser>>();
        var db = provider.GetRequiredService<DizidoDbContext>();
        var tokens = provider.GetRequiredService<IAccessTokenService>();
        var agora = provider.GetRequiredService<TimeProvider>().GetUtcNow();

        var id = Guid.CreateVersion7(agora);
        // Os últimos caracteres, e não os primeiros: um UUIDv7 começa com o timestamp em
        // milissegundos, então dois ids criados no mesmo instante têm prefixo idêntico. Um
        // "sufixo único" tirado do começo repetiria dentro do mesmo teste — a parte aleatória
        // fica no fim.
        var sufixo = id.ToString("N")[^6..];

        // O email leva o id inteiro porque ele é chave única no Identity, e mesmo seis
        // caracteres aleatórios acabam colidindo quando a suíte cresce.
        var email = $"{nome.ToLowerInvariant()}-{id:N}@dizido.test";

        var conta = new DizidoUser
        {
            Id = id,
            UserName = email,
            Email = email,
            CreatedAt = agora,
        };

        var resultado = await contas.CreateAsync(conta, "dizido-2026-teste");

        Assert.True(
            resultado.Succeeded,
            $"Não foi possível criar o usuário de teste: {string.Join("; ", resultado.Errors.Select(e => e.Description))}");

        db.Profiles.Add(UserProfile.Create(id, $"{nome} {sufixo}", agora));
        await db.SaveChangesAsync();

        return new Usuario(id, $"{nome} {sufixo}", tokens.Create(conta, agora));
    }

    /// <summary>Um HttpClient que já envia o token do usuário em toda requisição.</summary>
    public HttpClient ClienteDe(Usuario usuario)
    {
        var cliente = CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", usuario.Token);

        return cliente;
    }

    /// <summary>
    /// Implementação explícita porque <see cref="WebApplicationFactory{TEntryPoint}"/> já tem um
    /// <c>DisposeAsync</c> que devolve <c>ValueTask</c>, e o do xUnit devolve <c>Task</c>. Dois
    /// métodos de mesmo nome e assinatura não podem diferir só no tipo de retorno.
    /// </summary>
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>
/// Faz todas as classes de teste compartilharem o mesmo contêiner e o mesmo servidor.
/// </summary>
/// <remarks>
/// Sem isto o xUnit criaria uma instância da fábrica por classe de teste, e cada uma subiria
/// um PostgreSQL próprio — segundos de espera multiplicados por classe. Os testes continuam
/// isolados porque cada um cria os próprios usuários e grupos, com ids novos.
/// </remarks>
[CollectionDefinition(Nome)]
public sealed class ColecaoDaApi : ICollectionFixture<DizidoApiFactory>
{
    public const string Nome = "api";
}
