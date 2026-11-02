using Microsoft.AspNetCore.Mvc;

namespace PosTech.Execucao.Api.Controllers;

public sealed record CadastrarPecaRequest(
    string? Nome,
    string? Marca,
    string? Codigo,
    decimal Preco,
    int QuantidadeEmEstoque);

public sealed record EntradaDeEstoqueRequest(int Quantidade);

/// <summary>
/// O catálogo de peças e o saldo em estoque.
///
/// Mora no Execução porque é quem consome peça: a reserva acontece no
/// diagnóstico e a baixa na finalização, e as duas precisam do saldo na mesma
/// base. Com o estoque em outro serviço, cada diagnóstico viraria uma chamada
/// síncrona entre serviços (ver mapa-do-schema da Fase 4).
/// </summary>
[ApiController]
[Route("api/v1/pecas")]
public sealed class PecasController : ControllerBase
{
    private readonly IPecaRepositorio _pecas;

    public PecasController(IPecaRepositorio pecas)
    {
        _pecas = pecas;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar([FromQuery] bool incluirInativas, CancellationToken cancellationToken)
    {
        var pecas = await _pecas.ListarAsync(!incluirInativas, cancellationToken);

        return Ok(pecas.Select(Mapear));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(string id, CancellationToken cancellationToken)
    {
        var peca = await _pecas.ObterAsync(id, cancellationToken);

        return peca is null ? NotFound() : Ok(Mapear(peca));
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cadastrar([FromBody] CadastrarPecaRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Nome) || string.IsNullOrWhiteSpace(request.Codigo))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Nome e código são obrigatórios." });

        if (request.Preco < 0 || request.QuantidadeEmEstoque < 0)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Preço e estoque não podem ser negativos." });

        var peca = new Peca
        {
            Nome = request.Nome.Trim(),
            Marca = request.Marca?.Trim() ?? string.Empty,
            Codigo = request.Codigo.Trim(),
            Preco = request.Preco,
            QuantidadeEmEstoque = request.QuantidadeEmEstoque,
        };

        if (!await _pecas.InserirAsync(peca, cancellationToken))
            return Conflict(new ProblemDetails { Status = 409, Title = $"Já existe uma peça com o código '{peca.Codigo}'." });

        return CreatedAtAction(nameof(Obter), new { id = peca.Id }, Mapear(peca));
    }

    /// <summary>Chegou peça do fornecedor: soma ao estoque.</summary>
    [HttpPost("{id}/entrada")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DarEntrada(
        string id, [FromBody] EntradaDeEstoqueRequest request, CancellationToken cancellationToken)
    {
        if (request.Quantidade <= 0)
            return BadRequest(new ProblemDetails { Status = 400, Title = "A entrada tem que ser positiva." });

        var peca = await _pecas.DarEntradaAsync(id, request.Quantidade, cancellationToken);

        return peca is null ? NotFound() : Ok(Mapear(peca));
    }

    private static object Mapear(Peca p) => new
    {
        id = p.Id,
        nome = p.Nome,
        marca = p.Marca,
        codigo = p.Codigo,
        preco = p.Preco,
        quantidadeEmEstoque = p.QuantidadeEmEstoque,
        quantidadeReservada = p.QuantidadeReservada,
        disponivel = p.Disponivel,
        reservas = p.Reservas.Select(r => new { idOrdem = r.IdOrdem, quantidade = r.Quantidade, reservadaEm = r.ReservadaEm }),
        ativo = p.Ativo,
    };
}
