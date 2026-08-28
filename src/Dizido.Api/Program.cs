using Dizido.Api;
using Dizido.Api.Attachments;
using Dizido.Api.Auth;
using Dizido.Api.Endpoints;
using Dizido.Api.Health;
using Dizido.Api.Observabilidade;
using Dizido.Infrastructure;
using Dizido.Infrastructure.Identity;
using Dizido.Api.Realtime;
using Dizido.Infrastructure.Persistence;
using Dizido.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Observabilidade primeiro: assim um erro em qualquer registro abaixo já sai no formato certo,
// em vez de no logger padrão que seria substituído logo depois.
builder.UsarSerilog();
builder.UsarOpenTelemetry();

// Registra o DizidoDbContext e a conexão com o Postgres.
// A API não menciona Npgsql em lugar nenhum — isso é detalhe da Infrastructure.
builder.Services.AddDizidoInfrastructure(builder.Configuration);

// TimeProvider é a abstração de relógio do .NET 8+. As entidades recebem o instante como
// parâmetro em vez de chamarem DateTimeOffset.UtcNow por dentro — assim os testes injetam
// um relógio fixo e nenhum teste depende de quando foi executado.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHttpContextAccessor();

// Assina as URLs temporárias dos anexos. Scoped por hábito, não por necessidade — ele não
// guarda estado; o que guarda estado é o IObjectStorage, que é singleton na Infrastructure.
builder.Services.AddScoped<AttachmentPresenter>();

// ---------------------------------------------------------------------------
// Autenticação
// ---------------------------------------------------------------------------

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Seção 'Jwt' ausente na configuração.");

// Falhar no start é melhor do que emitir tokens assináveis por qualquer um. Um erro na
// inicialização é visível na hora; uma chave fraca em produção passa despercebida por meses.
if (jwtOptions.SigningKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey precisa ter ao menos 32 caracteres. Em produção, defina a variável "
        + "de ambiente Jwt__SigningKey — nunca reaproveite a chave de desenvolvimento.");
}

// A política mora em PoliticaDeSenha porque o teste de carga também cria contas, e ter as
// regras em dois lugares as faria divergir.
builder.Services
    .AddIdentityCore<DizidoUser>(PoliticaDeSenha.Aplicar)
    .AddEntityFrameworkStores<DizidoDbContext>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,

            // O padrão do .NET tolera 5 minutos de diferença de relógio — o que faria um token
            // de 15 min valer por 20. Zero exige relógios sincronizados (NTP), o que é o caso
            // em qualquer servidor moderno.
            ClockSkew = TimeSpan.Zero,
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // WebSocket não permite definir o cabeçalho Authorization no handshake, então
                // o cliente SignalR manda o token na query string. Sem isto, o hub da Fase 3
                // rejeitaria toda conexão. Restrito ao caminho do hub: aceitar token por query
                // string em toda a API o faria vazar em logs de servidor e histórico de navegação.
                var token = context.Request.Query["access_token"];

                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// A implementação provisória da Fase 1 (HeaderCurrentUser, que lia um cabeçalho forjável)
// foi substituída aqui. Nenhum endpoint mudou — todos pedem ICurrentUser, não o cabeçalho.
builder.Services.AddScoped<ICurrentUser, JwtCurrentUser>();
builder.Services.AddSingleton<IAccessTokenService, AccessTokenService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // ----- autenticação: por IP -----
    //
    // Sem isto, tentar senhas em sequência não custa nada ao atacante. Aqui a partição é o
    // IP porque quem tenta entrar ainda não tem identidade — é justamente o que ele procura.
    options.AddPolicy(LimitesDeUso.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
        }));

    // ----- envio de mensagem: por USUÁRIO -----
    //
    // Por usuário, e não por IP: uma escola, um escritório ou uma operadora móvel colocam
    // centenas de pessoas atrás do mesmo endereço. Limitar por IP puniria todas elas por
    // causa de uma, e o limite teria de ser tão alto que deixaria de limitar.
    //
    // Token bucket, e não janela fixa: colar um texto longo em três partes seguidas é uso
    // normal, e uma janela fixa recusaria a terceira. O balde acumula folga enquanto a pessoa
    // não escreve, permite a rajada, e ainda assim segura o ritmo sustentado.
    options.AddPolicy(LimitesDeUso.Mensagens, http => RateLimitPartition.GetTokenBucketLimiter(
        partitionKey: LimitesDeUso.ParticaoDe(http),
        factory: _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 30,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));

    // ----- upload: por USUÁRIO, e bem mais apertado -----
    //
    // Cada pedido autoriza até 50 MB e reserva uma linha no banco. Sem limite, um laço de
    // três linhas enche o bucket e a tabela de anexos de graça — e o custo é de quem hospeda.
    options.AddPolicy(LimitesDeUso.Uploads, http => RateLimitPartition.GetTokenBucketLimiter(
        partitionKey: LimitesDeUso.ParticaoDe(http),
        factory: _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(6),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));

    // Diz quanto esperar em vez de só recusar. Um cliente que sabe o tempo pode aguardar;
    // um que não sabe fica tentando, o que piora exatamente o problema que o limite resolve.
    options.OnRejected = async (contexto, ct) =>
    {
        if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
        {
            contexto.HttpContext.Response.Headers.RetryAfter =
                ((int)espera.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await contexto.HttpContext.Response.WriteAsJsonAsync(
            new { title = "Muitas requisições", detail = "Espere um pouco antes de tentar de novo." },
            ct);
    };
});

// ---------------------------------------------------------------------------
// Tempo real
// ---------------------------------------------------------------------------

var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Connection string 'Redis' ausente.");

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSingleton<IPresenceTracker, RedisPresenceTracker>();
builder.Services.AddSingleton<IConversationNotifier, ConversationNotifier>();

builder.Services
    .AddSignalR(options =>
    {
        // Em desenvolvimento, ver a exceção real no cliente economiza muito tempo.
        // Em produção isso vazaria stack trace, então fica preso ao ambiente.
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    })
    // O backplane. Com uma única instância ele não faz diferença nenhuma — está aqui porque
    // descobrir que falta ao subir a segunda instância, em produção, é péssimo: metade dos
    // usuários simplesmente para de receber mensagens da outra metade, sem erro nenhum no log.
    .AddStackExchangeRedis(redisConnection, options =>
        options.Configuration.ChannelPrefix = RedisChannel.Literal("dizido"));

// ---------------------------------------------------------------------------
// CORS
// ---------------------------------------------------------------------------

// Em desenvolvimento o Blazor roda em :5145 e a API em :5224 — origens diferentes, então o
// navegador exige CORS. Em produção os dois são servidos pela mesma origem e nada disso é usado.
//
// Note AllowCredentials() junto com origens EXPLÍCITAS: o navegador recusa, por especificação,
// combinar "*" com credenciais. É proposital — permitir qualquer origem enviar cookies
// autenticados seria entregar a sessão do usuário para qualquer site que ele visitasse.
var origensPermitidas = builder.Configuration.GetSection("Cors:Origens").Get<string[]>()
    ?? ["http://localhost:5145", "https://localhost:7247"];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(origensPermitidas)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---------------------------------------------------------------------------
// Saúde
// ---------------------------------------------------------------------------

// A tag separa o que o /health/ready confere do que o /health ignora. Sem ela, os dois
// endpoints rodariam as mesmas checagens e a distinção entre "reinicie" e "tire do balanceador"
// se perderia. Ver HealthEndpoints para o porquê de a diferença importar.
string[] prontidao = [HealthEndpoints.TagDeProntidao];

builder.Services.AddHealthChecks()
    // CanConnectAsync, e não uma consulta de verdade: o que se quer saber é se a conexão
    // sobe. Consultar uma tabela transformaria o health check em carga no banco.
    .AddDbContextCheck<DizidoDbContext>("postgres", tags: prontidao)
    .AddCheck<RedisHealthCheck>("redis", tags: prontidao)
    .AddCheck<StorageHealthCheck>("storage", tags: prontidao);

// ---------------------------------------------------------------------------

// ProblemDetails (RFC 9457) como formato padrão de erro em toda a API.
builder.Services.AddProblemDetails();

// A ordem importa: os handlers são tentados nesta sequência, e cada um devolve false
// para o que não é da sua alçada. O que nenhum tratar vira 500 — como deve ser.
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();

builder.Services.AddOpenApi();

var app = builder.Build();

// Modo "job de migração": aplica o que estiver pendente e encerra, sem abrir porta nenhuma.
// É assim que o deploy migra o banco — ver Migracoes para o porquê de não ser no start normal.
if (args.Contains(Migracoes.Argumento, StringComparer.Ordinal))
{
    return await Migracoes.AplicarAsync(app);
}

// O bucket precisa existir antes do primeiro upload, e criá-lo é idempotente. Fica aqui, e
// não numa migration ou num passo manual de instalação, porque é a única dependência do
// storage que a aplicação tem — e esquecer de criá-lo dá um erro obscuro na primeira foto.
//
// Diferente das migrations do banco: criar um bucket que já existe não é problema, aplicar a
// mesma migration duas vezes é.
await app.Services.GetRequiredService<IObjectStorage>().EnsureBucketAsync();

app.UseExceptionHandler();

// Depois do tratador de exceções, para que uma requisição que estourou apareça no log com o
// status final (500) e não com o que ela tinha antes de o tratador agir.
app.UsarLogDeRequisicoes();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// CORS antes de autenticação: a requisição de sondagem (OPTIONS) que o navegador manda
// não carrega credenciais, e precisa ser respondida antes de qualquer verificação de token.
app.UseCors();

// Ordem obrigatória: autenticação ("quem é você") antes de autorização ("você pode?").
// Invertido, a autorização rodaria sem saber quem é o usuário e negaria tudo.
app.UseAuthentication();
app.UseAuthorization();

// DEPOIS da autenticação, e isto não é detalhe de arrumação.
//
// As políticas de mensagem e upload particionam a contagem pelo id do usuário, lido de
// HttpContext.User. Com o limitador antes do UseAuthentication, esse User ainda está vazio:
// todas as requisições cairiam na partição de IP, e um escritório inteiro dividiria a mesma
// cota. Pior, nada falharia — o limite continuaria "funcionando", só que na chave errada.
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new { service = "Dizido.Api", status = "ok" }))
   .ExcludeFromDescription()
   .AllowAnonymous();

// Anônimos de propósito: o orquestrador consulta antes de haver qualquer sessão, e ele não
// tem como se autenticar. É por isso que a resposta não carrega stack trace.
app.MapHealthEndpoints();

app.MapAuthEndpoints().RequireRateLimiting(LimitesDeUso.Auth);

// RequireAuthorization no grupo inteiro: qualquer endpoint novo já nasce protegido.
// O contrário — proteger um por um — significa que esquecer uma linha abre um buraco em
// silêncio, e ninguém percebe até alguém encontrar.
app.MapUserEndpoints().RequireAuthorization();
app.MapConversationEndpoints().RequireAuthorization();
app.MapMessageEndpoints().RequireAuthorization();
app.MapSyncEndpoints().RequireAuthorization();
app.MapGroupEndpoints().RequireAuthorization();
app.MapAttachmentEndpoints().RequireAuthorization();
app.MapSearchEndpoints().RequireAuthorization();

// O hub fica em /hubs/chat — o mesmo prefixo que o JwtBearerEvents aceita token por
// query string, porque WebSocket nao permite cabecalho Authorization no handshake.
app.MapHub<ChatHub>("/hubs/chat").RequireAuthorization();

await app.RunAsync();

// O `return` explícito existe porque o modo de migração acima devolve um código de saída.
// Sem ele, o compilador não aceitaria os dois caminhos: um que retorna int e outro que não.
return 0;

/// <summary>
/// Necessária para os testes de integração acessarem a classe Program gerada implicitamente
/// pelas top-level statements (WebApplicationFactory&lt;Program&gt;).
/// </summary>
public partial class Program;
