using Dizido.Client;
using Dizido.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Endereço da API. Em desenvolvimento o Blazor roda numa porta e a API em outra;
// em produção os dois são servidos pela mesma origem e isto vira builder.HostEnvironment.BaseAddress.
var apiBaseAddress = new Uri(builder.Configuration["ApiBaseAddress"] ?? "http://localhost:5224/");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DizidoSession>();

// O handler que injeta o token e renova a sessão. Registrado como Transient porque
// o HttpClientFactory gerencia o ciclo de vida da cadeia de handlers.
builder.Services.AddTransient<AuthorizationHandler>();

builder.Services
    .AddHttpClient<DizidoApiClient>(client => client.BaseAddress = apiBaseAddress)
    .AddHttpMessageHandler<AuthorizationHandler>()
    .ConfigurePrimaryHttpMessageHandler(() => new BrowserHttpHandler());

builder.Services.AddSingleton(sp => new ChatConnection(
    sp.GetRequiredService<DizidoSession>(),
    apiBaseAddress));

// Singleton, e não Scoped: um Singleton (Outbox) não pode depender de um Scoped — seria uma
// "dependência cativa", em que o serviço de vida curta fica preso ao de vida longa e nunca é
// descartado. No Blazor WebAssembly os dois escopos coincidem na prática (há um só usuário por
// aplicação), mas o contêiner valida isso e o mesmo código no servidor seria um vazamento real.
builder.Services.AddSingleton<IArmazenamentoLocal, ArmazenamentoDoNavegador>();
builder.Services.AddSingleton<Outbox>();
builder.Services.AddSingleton<ChatStore>();

await builder.Build().RunAsync();
