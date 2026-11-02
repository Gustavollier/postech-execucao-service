using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Execucao.Api.Aplicacao;

/// <summary>
/// O que o Execução faz quando o barramento traz alguma coisa.
///
/// Três eventos interessam: a ordem aberta (entra na fila), o pagamento
/// confirmado (libera a bancada) e a ordem cancelada (sai da fila e devolve as
/// peças). A regra de cada um mora no <see cref="ServicoDaOficina"/>; aqui só
/// se lê o envelope e se registra o que aconteceu.
/// </summary>
public sealed class ManipuladorDeEventos
{
    private readonly ServicoDaOficina _oficina;
    private readonly ILogger<ManipuladorDeEventos> _log;

    public ManipuladorDeEventos(ServicoDaOficina oficina, ILogger<ManipuladorDeEventos> log)
    {
        _oficina = oficina;
        _log = log;
    }

    public async Task OrdemAbertaAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        var evento = envelope.Ler<OrdemAberta>()
            ?? throw new InvalidOperationException("OrdemAberta veio sem corpo.");

        if (await _oficina.ReceberAsync(evento, cancellationToken).ConfigureAwait(false))
            _log.LogInformation("Ordem {IdOrdem} entrou na fila da oficina.", evento.IdOrdem);
        else
            _log.LogInformation("Ordem {IdOrdem} já estava na fila. Nada a fazer.", evento.IdOrdem);
    }

    public async Task PagamentoConfirmadoAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        var evento = envelope.Ler<PagamentoConfirmado>()
            ?? throw new InvalidOperationException("PagamentoConfirmado veio sem corpo.");

        var resultado = await _oficina.LiberarAsync(evento.IdOrdem, cancellationToken).ConfigureAwait(false);

        switch (resultado)
        {
            case EResultadoDoEvento.Aplicado:
                _log.LogInformation("Ordem {IdOrdem} liberada para execução.", evento.IdOrdem);
                break;
            case EResultadoDoEvento.JaAplicado:
                _log.LogInformation("Ordem {IdOrdem} já estava liberada.", evento.IdOrdem);
                break;
            default:
                _log.LogWarning(
                    "Pagamento confirmado para a ordem {IdOrdem} descartado: {Resultado}.",
                    evento.IdOrdem, resultado);
                break;
        }
    }

    public async Task OrdemCanceladaAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        var evento = envelope.Ler<OrdemCancelada>()
            ?? throw new InvalidOperationException("OrdemCancelada veio sem corpo.");

        var resultado = await _oficina.CancelarAsync(evento.IdOrdem, evento.Motivo, cancellationToken)
            .ConfigureAwait(false);

        if (resultado == EResultadoDoEvento.Ignorado)
            _log.LogWarning(
                "Cancelamento da ordem {IdOrdem} ignorado: o trabalho já estava finalizado.", evento.IdOrdem);
        else
            _log.LogWarning(
                "Ordem {IdOrdem} cancelada ({Resultado}) e peças devolvidas: {Motivo}.",
                evento.IdOrdem, resultado, evento.Motivo);
    }

    /// <summary>Os tratadores que o consumidor registra na fila do Execução.</summary>
    public IReadOnlyDictionary<string, TratadorDeEvento> Tratadores() => new Dictionary<string, TratadorDeEvento>
    {
        ["OrdemAberta"] = OrdemAbertaAsync,
        ["PagamentoConfirmado"] = PagamentoConfirmadoAsync,
        ["OrdemCancelada"] = OrdemCanceladaAsync,
    };
}
