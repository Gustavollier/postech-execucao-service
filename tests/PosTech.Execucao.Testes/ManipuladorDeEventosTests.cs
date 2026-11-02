using Microsoft.Extensions.Logging;
using PosTech.Contratos;
using PosTech.Execucao;
using PosTech.Execucao.Api.Aplicacao;
using PosTech.Mensageria;

namespace PosTech.Execucao.Testes;

/// <summary>
/// O manipulador, nos casos que a fila produz e o caminho feliz não mostra:
/// pagamento de ordem que não está aqui, entrega repetida, cancelamento depois
/// do trabalho pronto.
///
/// O que se exige de todos eles é o mesmo: <b>não lançar</b>. Lançar aqui faz o
/// consumidor tentar de novo e depois mandar para a dead letter uma mensagem
/// que não tem nada de errado — só chegou num estado em que não há o que
/// fazer. O registro em log é o que sobra para quem investigar.
/// </summary>
[Collection("execucao")]
public class ManipuladorDeEventosTests
{
    private readonly OrdemRepositorio _ordens;
    private readonly ServicoDaOficina _oficina;
    private readonly LogEmMemoria<ManipuladorDeEventos> _log = new();
    private readonly ManipuladorDeEventos _manipulador;

    public ManipuladorDeEventosTests(AmbienteDaExecucao ambiente)
    {
        var banco = ambiente.BancoIsolado();

        _ordens = new OrdemRepositorio(banco);
        _ordens.GarantirIndicesAsync().GetAwaiter().GetResult();

        _oficina = new ServicoDaOficina(_ordens, new PecaRepositorio(banco));
        _manipulador = new ManipuladorDeEventos(_oficina, _log);
    }

    private static EnvelopeRecebido Envelope<T>(T evento) where T : notnull
    {
        var saida = Outbox.Enfileirar(evento, "corr-teste", "os-service");

        return new EnvelopeRecebido(saida.MensagemId, saida.Tipo, saida.RoutingKey, saida.CorrelationId, saida.Payload);
    }

    private static OrdemAberta Aberta(int idOrdem) =>
        new(idOrdem, 10, "Ana Prado", "ana@oficina.test", 20, "Gol AAA-1234", 30, DateTime.UtcNow);

    private static ItemPedido MaoDeObra() => new("Troca de óleo", ETipoItem.MaoDeObra, null, 1, 100m);

    [Fact]
    public async Task Os_tres_eventos_assinados_tem_tratador()
    {
        // A lista do manipulador é a que o consumidor registra. Evento
        // assinado na topologia e esquecido aqui nunca seria tratado.
        Assert.Equal(
            Topologia.Assinaturas[Topologia.FilaExecucao].Length,
            _manipulador.Tratadores().Count);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Abertura_repetida_e_registrada_e_nao_lanca()
    {
        await _manipulador.OrdemAbertaAsync(Envelope(Aberta(1)));
        await _manipulador.OrdemAbertaAsync(Envelope(Aberta(1)));

        Assert.Contains(_log.Mensagens, m => m.Contains("já estava na fila"));
    }

    [Fact]
    public async Task Pagamento_de_ordem_desconhecida_e_descartado_com_aviso()
    {
        await _manipulador.PagamentoConfirmadoAsync(
            Envelope(new PagamentoConfirmado(404, "pag", 10m, "mp", DateTime.UtcNow)));

        Assert.Contains(_log.Avisos, m => m.Contains("OrdemDesconhecida"));
    }

    [Fact]
    public async Task Pagamento_repetido_e_registrado_e_nao_lanca()
    {
        await _oficina.ReceberAsync(Aberta(2));
        await _oficina.DiagnosticarAsync(2, [MaoDeObra()], "corr-2");

        var pagamento = new PagamentoConfirmado(2, "pag", 100m, "mp", DateTime.UtcNow);

        await _manipulador.PagamentoConfirmadoAsync(Envelope(pagamento));
        await _manipulador.PagamentoConfirmadoAsync(Envelope(pagamento));

        Assert.Contains(_log.Mensagens, m => m.Contains("liberada para execução"));
        Assert.Contains(_log.Mensagens, m => m.Contains("já estava liberada"));
        Assert.Equal(EEtapaDeExecucao.EmExecucao, (await _ordens.ObterPorOrdemAsync(2))!.Etapa);
    }

    [Fact]
    public async Task Cancelamento_de_trabalho_pronto_e_ignorado_com_aviso()
    {
        await _oficina.ReceberAsync(Aberta(3));
        await _oficina.DiagnosticarAsync(3, [MaoDeObra()], "corr-3");
        await _oficina.LiberarAsync(3);
        await _oficina.FinalizarAsync(3, "corr-3");

        await _manipulador.OrdemCanceladaAsync(Envelope(new OrdemCancelada(3, "timeout", DateTime.UtcNow)));

        Assert.Contains(_log.Avisos, m => m.Contains("já estava finalizado"));
        Assert.Equal(EEtapaDeExecucao.Finalizada, (await _ordens.ObterPorOrdemAsync(3))!.Etapa);
    }

    [Fact]
    public async Task Envelope_sem_corpo_lanca_para_ir_a_dead_letter()
    {
        // Este é o caso em que lançar é o certo: a mensagem está quebrada, e
        // a dead letter é onde alguém vai olhar.
        var vazio = new EnvelopeRecebido(Guid.NewGuid(), "OrdemAberta", RoutingKeys.OrdemAberta, "corr", "null");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manipulador.OrdemAbertaAsync(vazio));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manipulador.PagamentoConfirmadoAsync(vazio));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manipulador.OrdemCanceladaAsync(vazio));
    }
}

/// <summary>Logger que guarda o que foi escrito, para o teste conferir.</summary>
public sealed class LogEmMemoria<T> : ILogger<T>
{
    private readonly List<(LogLevel Nivel, string Mensagem)> _entradas = [];
    private readonly object _trava = new();

    public IReadOnlyList<string> Mensagens
    {
        get { lock (_trava) return _entradas.Select(e => e.Mensagem).ToList(); }
    }

    public IReadOnlyList<string> Avisos
    {
        get { lock (_trava) return _entradas.Where(e => e.Nivel >= LogLevel.Warning).Select(e => e.Mensagem).ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_trava)
            _entradas.Add((logLevel, formatter(state, exception)));
    }
}
