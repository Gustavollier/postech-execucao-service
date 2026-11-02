using Microsoft.AspNetCore.Mvc;
using PosTech.Contratos;

namespace PosTech.Execucao.Api.Controllers;

public sealed record ItemDoDiagnosticoRequest(
    string? Descricao,
    ETipoItem Tipo,
    string? IdPeca,
    int Quantidade,
    decimal ValorUnitario);

public sealed record DiagnosticoRequest(IReadOnlyList<ItemDoDiagnosticoRequest>? Itens);

/// <summary>
/// A bancada: o que o mecânico vê e faz.
///
/// A ordem entra aqui pela fila (<c>OrdemAberta</c>), e não por HTTP. O que o
/// mecânico faz por HTTP é diagnosticar e finalizar — os dois passos que geram
/// fato novo para o resto da saga.
/// </summary>
[ApiController]
[Route("api/v1/execucao")]
public sealed class OrdensController : ControllerBase
{
    private readonly IOrdemRepositorio _ordens;
    private readonly ServicoDaOficina _oficina;

    public OrdensController(IOrdemRepositorio ordens, ServicoDaOficina oficina)
    {
        _ordens = ordens;
        _oficina = oficina;
    }

    /// <summary>
    /// A fila da oficina, na ordem de atendimento: primeiro o que já está na
    /// bancada, depois o que espera diagnóstico, depois o que espera
    /// pagamento. <c>?etapa=</c> filtra por uma etapa só.
    /// </summary>
    [HttpGet("fila")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Fila([FromQuery] EEtapaDeExecucao? etapa, CancellationToken cancellationToken)
    {
        var ordens = await _ordens.ListarFilaAsync(etapa, cancellationToken);

        return Ok(ordens.Select(Mapear));
    }

    [HttpGet("ordens/{idOrdem:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PorOrdem(int idOrdem, CancellationToken cancellationToken)
    {
        var ordem = await _ordens.ObterPorOrdemAsync(idOrdem, cancellationToken);

        return ordem is null ? NotFound() : Ok(Mapear(ordem));
    }

    /// <summary>
    /// Fecha o diagnóstico: lança os itens, reserva as peças e publica
    /// <c>DiagnosticoConcluido</c> com os itens dentro — que é de onde o
    /// Billing monta o orçamento.
    ///
    /// Peça leva o preço do catálogo, não o da requisição. Sem estoque, 409 e
    /// nenhuma peça fica reservada.
    /// </summary>
    [HttpPost("ordens/{idOrdem:int}/diagnostico")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Diagnosticar(
        int idOrdem, [FromBody] DiagnosticoRequest request, CancellationToken cancellationToken)
    {
        var pedidos = (request.Itens ?? [])
            .Select(i => new ItemPedido(i.Descricao, i.Tipo, i.IdPeca, i.Quantidade, i.ValorUnitario))
            .ToList();

        var ordem = await _oficina.DiagnosticarAsync(idOrdem, pedidos, CorrelationId(), cancellationToken);

        return Ok(Mapear(ordem));
    }

    /// <summary>
    /// O trabalho terminou: as peças reservadas saem do estoque e
    /// <c>ExecucaoFinalizada</c> vai para o orquestrador, que fecha a ordem.
    /// Chamar de novo devolve a ordem como está.
    /// </summary>
    [HttpPost("ordens/{idOrdem:int}/finalizar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Finalizar(int idOrdem, CancellationToken cancellationToken)
    {
        var ordem = await _oficina.FinalizarAsync(idOrdem, CorrelationId(), cancellationToken);

        return Ok(Mapear(ordem));
    }

    private static object Mapear(OrdemEmExecucao o) => new
    {
        idOrdem = o.IdOrdem,
        etapa = o.Etapa,
        idCliente = o.IdCliente,
        nomeCliente = o.NomeCliente,
        idVeiculo = o.IdVeiculo,
        descricaoVeiculo = o.DescricaoVeiculo,
        idFuncionarioResponsavel = o.IdFuncionarioResponsavel,
        itens = o.Itens.Select(i => new
        {
            descricao = i.Descricao,
            tipo = i.Tipo,
            idPeca = i.IdPeca,
            quantidade = i.Quantidade,
            valorUnitario = i.ValorUnitario,
            subtotal = i.Subtotal,
        }),
        valorTotal = o.ValorTotal,
        motivoDoCancelamento = o.MotivoDoCancelamento,
        abertaEm = o.AbertaEm,
        diagnosticadaEm = o.DiagnosticadaEm,
        finalizadaEm = o.FinalizadaEm,
    };

    /// <summary>
    /// O <c>X-Correlation-ID</c> que o APIM propaga, ou um novo. É ele que vai
    /// no evento publicado e amarra os logs dos quatro serviços.
    /// </summary>
    private string CorrelationId() =>
        Request.Headers.TryGetValue("X-Correlation-ID", out var valor) && !string.IsNullOrWhiteSpace(valor)
            ? valor.ToString()
            : Guid.NewGuid().ToString();
}
