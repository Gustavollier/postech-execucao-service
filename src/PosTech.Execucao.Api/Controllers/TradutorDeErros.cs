using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PosTech.Execucao.Api.Controllers;

/// <summary>
/// Transforma as falhas de negócio em respostas HTTP, num lugar só.
///
/// Os casos de uso lançam exceções com tipo — ordem que não existe, estoque
/// que não dá, gravação que perdeu a corrida — e cada uma tem um status certo.
/// Espalhar <c>try/catch</c> por cada ação dos controllers faria um esquecer o
/// que o outro trata, e o esquecido vira 500.
/// </summary>
public sealed class TradutorDeErros : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, titulo, extras) = context.Exception switch
        {
            OrdemNaoEncontradaException e => (StatusCodes.Status404NotFound, e.Message, null),
            PecaNaoEncontradaException e => (StatusCodes.Status422UnprocessableEntity, e.Message,
                new Dictionary<string, object?> { ["idPeca"] = e.IdPeca }),
            EstoqueInsuficienteException e => (StatusCodes.Status409Conflict, e.Message,
                new Dictionary<string, object?> { ["idPeca"] = e.IdPeca, ["disponivel"] = e.Disponivel, ["pedido"] = e.Pedido }),
            ConflitoDeConcorrenciaException e => (StatusCodes.Status409Conflict, e.Message, null),
            ArgumentException e => (StatusCodes.Status400BadRequest, e.Message, null),

            // As transições inválidas da ordem: diagnosticar duas vezes,
            // finalizar o que não foi liberado, reservar peça inativa.
            InvalidOperationException e => (StatusCodes.Status409Conflict, e.Message, (Dictionary<string, object?>?)null),

            _ => (0, string.Empty, null),
        };

        if (status == 0)
            return;

        var problema = new ProblemDetails { Status = status, Title = titulo };

        foreach (var (chave, valor) in extras ?? [])
            problema.Extensions[chave] = valor;

        context.Result = new ObjectResult(problema) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
