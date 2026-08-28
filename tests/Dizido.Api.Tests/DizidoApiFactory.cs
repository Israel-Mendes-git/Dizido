using System.Net.Http.Headers;
using Dizido.Api.Auth;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Identity;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Dizido.Api.Tests;

/// <summary>
/// Sobe a API inteira em memória, ligada a um PostgreSQL, um Redis e um MinIO de verdade,
/// todos em contêineres descartáveis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nada é substituído.</b> Não há um único serviço trocado por versão de teste: o que roda é
/// o <c>Program.cs</c> de produção, com os mesmos endpoints, o mesmo pipeline de autenticação e
/// as mesmas dependências. Se um teste passa aqui, passou contra o sistema, não contra uma
/// maquete dele.
/// </para>
/// <para>
/// <b>Por que dependências reais e não falsas.</b> Trocar o provedor por InMemory ou SQLite
/// testaria outro banco: a paginação por cursor usa SQL cru, o índice único de deduplicação é do
/// Postgres, e <c>DateTimeOffset</c> tem tradução própria no Npgsql. No storage é a mesma coisa —
/// um falso em memória diria "sim" para a URL assinada sem provar que ela vale.
/// </para>
/// <para>
/// As imagens são as mesmas do <c>docker-compose.yml</c> de propósito, e os contêineres ganham
/// porta aleatória: não conflitam com o ambiente de desenvolvimento nem deixam sujeira entre
/// execuções.
/// </para>
/// <para>
/// O preço é depender do Docker para rodar <c>dotnet test</c>. Os testes de domínio continuam
/// rodando sozinhos, em milissegundos, e são onde mora a maior parte das regras.
/// </para>
/// </remarks>
public sealed class DizidoApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // A mesma imagem do docker-compose.yml: testar contra outra versão do banco enfraquece o teste.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    // MinIO de verdade também, e pela mesma razão do banco. A parte que mais interessa
    // testar no upload é o que acontece entre a API e o storage: a URL assinada vale, os
    // bytes que chegaram são os que o servidor lê, o objeto recusado some do bucket. Um
    // storage falso em memória responderia "sim" para tudo isso sem provar nada.
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:latest").Build();

    // O terceiro contêiner entrou na Fase 8 e pagou o próprio custo: com ele, a suíte deixou
    // de substituir a presença e o backplane do SignalR por versões de mentira. O que roda
    // agora é o Program.cs inteiro, sem um único serviço trocado — e é isso que faz o
    // /health/ready ser testável de verdade, com as três dependências no ar.
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

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
                ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),

                ["Storage:Endpoint"] = $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}",
                ["Storage:AccessKey"] = _minio.GetAccessKey(),
                ["Storage:SecretKey"] = _minio.GetSecretKey(),
                ["Storage:Bucket"] = "dizido-testes",
            }));
    }

    public async Task InitializeAsync()
    {
        // Os três em paralelo: são independentes, e esperar um depois do outro triplicaria o
        // tempo de arranque da suíte.
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync(), _redis.StartAsync());

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
        await _minio.DisposeAsync();
        await _redis.DisposeAsync();
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
