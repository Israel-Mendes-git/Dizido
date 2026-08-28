using Microsoft.AspNetCore.Diagnostics;

namespace Dizido.Api;

/// <summary>
/// Traduz <see cref="BadHttpRequestException"/> em resposta com o status que ela já carrega
/// (tipicamente 400), em vez de deixar virar 500.
/// </summary>
/// <remarks>
/// O caso comum é JSON malformado: mandar <c>"clientMessageId": ""</c> onde se espera um Guid
/// faz o binding do Minimal API lançar essa exceção. Sem este handler, o
/// <c>UseExceptionHandler</c> não reconhece o tipo, cai no tratamento genérico e responde 500 —
/// o que faria o cliente concluir "o servidor está quebrado" quando na verdade a requisição
/// dele é que estava errada. E, sendo 500, o cliente tentaria de novo, indefinidamente.
/// <para>
/// Handlers de exceção formam uma cadeia: cada um devolve <c>false</c> para o que não é seu,
/// e o próximo tenta. Este e o <see cref="DomainExceptionHandler"/> convivem sem se atrapalhar.
/// </para>
/// </remarks>
internal sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        context.Response.StatusCode = badRequest.StatusCode;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Title = "Requisição inválida",
                Detail = badRequest.Message,
                Status = badRequest.StatusCode,
            },
        });
    }
}
