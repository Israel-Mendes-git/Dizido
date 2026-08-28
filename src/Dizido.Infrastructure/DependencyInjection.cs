using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dizido.Infrastructure;

/// <summary>
/// Ponto único de registro dos serviços de infraestrutura no contêiner de DI.
/// </summary>
/// <remarks>
/// A API chama <c>builder.Services.AddDizidoInfrastructure(config)</c> e pronto — ela não
/// precisa saber que existe Npgsql, nem qual é o nome da connection string. Trocar Postgres
/// por outro banco um dia mexe só neste arquivo.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddDizidoInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Dizido")
            ?? throw new InvalidOperationException(
                "Connection string 'Dizido' não encontrada. Confira appsettings.Development.json.");

        services.AddDbContext<DizidoDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                // Migrations moram na Infrastructure, junto do DbContext.
                npgsql.MigrationsAssembly(typeof(DizidoDbContext).Assembly.FullName);

                // Retry automático em falhas transitórias (rede oscilando, banco reiniciando).
                // Sem isso, um blip de 200 ms na conexão vira erro 500 para o usuário.
                npgsql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null);
            });
        });

        return services;
    }
}
