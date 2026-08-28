using Dizido.Domain;
using Microsoft.AspNetCore.Diagnostics;

namespace Dizido.Api;

/// <summary>
/// Traduz <see cref="DomainException"/> em HTTP 400 com corpo ProblemDetails.
/// </summary>
/// <remarks>
/// Sem isto, cada endpoint precisaria de um try/catch idêntico — e o que ninguém envolver
/// vaza stack trace em produção. Concentrando aqui, a regra de negócio simplesmente lança,
/// e a tradução para HTTP acontece uma vez só.
/// <para>
/// ProblemDetails (RFC 9457) é o formato padronizado de erro em HTTP: os clientes sabem ler
/// <c>title</c>/<c>detail</c>/<c>status</c> sem precisar de um parser específico do Dizido.
/// </para>
/// </remarks>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException)
        {
            // Não é erro de regra de negócio: deixa passar para o handler padrão,
            // que devolve 500 e registra o erro sem expor detalhes ao cliente.
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Title = "Regra de negócio violada",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest,
            },
        });
    }
}
