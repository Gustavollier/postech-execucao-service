using PosTech.Contratos;
using PosTech.Execucao;
using PosTech.Mensageria;

namespace PosTech.Execucao.Testes;

/// <summary>
/// Os casos de uso da oficina contra MongoDB de verdade.
///
/// O que importa aqui é o que acontece entre os dois documentos — a ordem e
/// as peças — que não têm transação entre si: o diagnóstico que falha no meio
/// não pode deixar peça presa, e a compensação tem que devolver tudo.
/// </summary>
[Collection("execucao")]
public class ServicoDaOficinaTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly OrdemRepositorio _ordens;
    private readonly PecaRepositorio _pecas;
    private readonly ServicoDaOficina _oficina;

    public ServicoDaOficinaTests(AmbienteDaExecucao ambiente)
    {
        var banco = ambiente.BancoIsolado();

        _ordens = new OrdemRepositorio(banco);
        _pecas = new PecaRepositorio(banco);
        _ordens.GarantirIndicesAsync().GetAwaiter().GetResult();
        _pecas.GarantirIndicesAsync().GetAwaiter().GetResult();

        _oficina = new ServicoDaOficina(_ordens, _pecas, () => Agora);
    }

    private static OrdemAberta Aberta(int idOrdem) =>
        new(idOrdem, 10, "Ana Prado", "ana@oficina.test", 20, "Gol AAA-1234", 30, Agora);

    private async Task<Peca> PecaAsync(int estoque = 10, decimal preco = 38.90m, string nome = "Filtro de óleo")
    {
        var peca = new Peca { Nome = nome, Codigo = $"P-{Guid.NewGuid():N}", Preco = preco, QuantidadeEmEstoque = estoque };
        await _pecas.InserirAsync(peca);
        return peca;
    }

    private static ItemPedido MaoDeObra(decimal valor = 120.50m) =>
        new("Troca de óleo", ETipoItem.MaoDeObra, null, 1, valor);

    private static ItemPedido PecaPedida(string idPeca, int quantidade, decimal valorQueOMecanicoDigitou = 0m) =>
        new(null, ETipoItem.Peca, idPeca, quantidade, valorQueOMecanicoDigitou);

    private async Task<OrdemEmExecucao> DiagnosticadaAsync(int idOrdem, params ItemPedido[] itens)
    {
        await _oficina.ReceberAsync(Aberta(idOrdem));
        return await _oficina.DiagnosticarAsync(idOrdem, itens.Length > 0 ? itens : [MaoDeObra()], $"corr-{idOrdem}");
    }

    [Fact]
    public async Task Receber_a_mesma_ordem_duas_vezes_coloca_uma_so_na_fila()
    {
        Assert.True(await _oficina.ReceberAsync(Aberta(1)));
        Assert.False(await _oficina.ReceberAsync(Aberta(1)));

        Assert.Single(await _ordens.ListarFilaAsync());
    }

    [Fact]
    public async Task O_preco_da_peca_sai_do_catalogo_e_nao_da_requisicao()
    {
        // Quem lança o item não escolhe o preço da peça: o orçamento sai daqui.
        var filtro = await PecaAsync(preco: 38.90m);

        var ordem = await DiagnosticadaAsync(2, MaoDeObra(120.50m), PecaPedida(filtro.Id, 2, valorQueOMecanicoDigitou: 1m));

        var item = Assert.Single(ordem.Itens, i => i.Tipo == ETipoItem.Peca);
        Assert.Equal(38.90m, item.ValorUnitario);
        Assert.Equal("Filtro de óleo", item.Descricao);
        Assert.Equal(198.30m, ordem.ValorTotal);

        Assert.Equal(2, (await _pecas.ObterAsync(filtro.Id))!.QuantidadeReservada);
        Assert.Equal("DiagnosticoConcluido", Assert.Single(ordem.EventosPendentes).Tipo);
    }

    [Fact]
    public async Task A_mesma_peca_em_duas_linhas_e_reservada_somada()
    {
        var filtro = await PecaAsync(estoque: 3);

        await DiagnosticadaAsync(3, PecaPedida(filtro.Id, 1), PecaPedida(filtro.Id, 2));

        var reserva = Assert.Single((await _pecas.ObterAsync(filtro.Id))!.Reservas);
        Assert.Equal(3, reserva.Quantidade);
    }

    [Fact]
    public async Task Se_uma_peca_falta_as_outras_reservadas_voltam_e_a_ordem_nao_muda()
    {
        // Ou o diagnóstico inteiro passa, ou o estoque fica como estava.
        var filtro = await PecaAsync(estoque: 10);
        var pastilha = await PecaAsync(estoque: 1, nome: "Pastilha");
        await _oficina.ReceberAsync(Aberta(4));

        await Assert.ThrowsAsync<EstoqueInsuficienteException>(() => _oficina.DiagnosticarAsync(
            4, [PecaPedida(filtro.Id, 2), PecaPedida(pastilha.Id, 4)], "corr-4"));

        Assert.Empty((await _pecas.ObterAsync(filtro.Id))!.Reservas);
        Assert.Empty((await _pecas.ObterAsync(pastilha.Id))!.Reservas);

        var ordem = (await _ordens.ObterPorOrdemAsync(4))!;
        Assert.Equal(EEtapaDeExecucao.AguardandoDiagnostico, ordem.Etapa);
        Assert.Empty(ordem.EventosPendentes);

        // E dá para tentar de novo, com o que tem.
        var refeita = await _oficina.DiagnosticarAsync(4, [PecaPedida(filtro.Id, 2), PecaPedida(pastilha.Id, 1)], "corr-4");
        Assert.Equal(EEtapaDeExecucao.AguardandoLiberacao, refeita.Etapa);
    }

    [Fact]
    public async Task Peca_que_nao_existe_falha_antes_de_reservar_qualquer_coisa()
    {
        var filtro = await PecaAsync();
        await _oficina.ReceberAsync(Aberta(5));

        await Assert.ThrowsAsync<PecaNaoEncontradaException>(() => _oficina.DiagnosticarAsync(
            5, [PecaPedida(filtro.Id, 1), PecaPedida("nao-existe", 1)], "corr-5"));

        Assert.Empty((await _pecas.ObterAsync(filtro.Id))!.Reservas);
    }

    [Fact]
    public async Task Item_invalido_falha_antes_de_reservar()
    {
        var filtro = await PecaAsync();
        await _oficina.ReceberAsync(Aberta(6));

        var maoDeObraComPeca = new ItemPedido("Mão de obra", ETipoItem.MaoDeObra, filtro.Id, 1, 100m);

        await Assert.ThrowsAsync<ArgumentException>(() => _oficina.DiagnosticarAsync(
            6, [PecaPedida(filtro.Id, 1), maoDeObraComPeca], "corr-6"));

        Assert.Empty((await _pecas.ObterAsync(filtro.Id))!.Reservas);
    }

    [Fact]
    public async Task Diagnosticar_ordem_que_nao_esta_na_fila_diz_qual()
    {
        var erro = await Assert.ThrowsAsync<OrdemNaoEncontradaException>(
            () => _oficina.DiagnosticarAsync(404, [MaoDeObra()], "corr"));

        Assert.Equal(404, erro.IdOrdem);
    }

    [Fact]
    public async Task Clique_duplo_no_diagnostico_publica_um_e_reserva_uma_vez()
    {
        // As duas chamadas leem a ordem esperando diagnóstico. A versão é o
        // que decide quem ganha; a perdedora não pode devolver a reserva que a
        // vencedora fez para a mesma ordem.
        var filtro = await PecaAsync(estoque: 10);
        await _oficina.ReceberAsync(Aberta(7));

        var chamadas = Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await _oficina.DiagnosticarAsync(7, [PecaPedida(filtro.Id, 2)], "corr-7");
                return true;
            }
            catch (Exception e) when (e is ConflitoDeConcorrenciaException or InvalidOperationException)
            {
                return false;
            }
        });

        var resultados = await Task.WhenAll(chamadas);

        Assert.Equal(1, resultados.Count(r => r));

        var ordem = (await _ordens.ObterPorOrdemAsync(7))!;
        Assert.Equal(EEtapaDeExecucao.AguardandoLiberacao, ordem.Etapa);
        Assert.Single(ordem.EventosPendentes);

        var reserva = Assert.Single((await _pecas.ObterAsync(filtro.Id))!.Reservas);
        Assert.Equal(2, reserva.Quantidade);
    }

    [Fact]
    public async Task Liberar_responde_para_cada_estado_da_ordem()
    {
        Assert.Equal(EResultadoDoEvento.OrdemDesconhecida, await _oficina.LiberarAsync(800));

        await _oficina.ReceberAsync(Aberta(8));
        Assert.Equal(EResultadoDoEvento.Ignorado, await _oficina.LiberarAsync(8));

        await _oficina.DiagnosticarAsync(8, [MaoDeObra()], "corr-8");
        Assert.Equal(EResultadoDoEvento.Aplicado, await _oficina.LiberarAsync(8));
        Assert.Equal(EResultadoDoEvento.JaAplicado, await _oficina.LiberarAsync(8));

        Assert.Equal(EEtapaDeExecucao.EmExecucao, (await _ordens.ObterPorOrdemAsync(8))!.Etapa);
    }

    [Fact]
    public async Task Pagamento_de_ordem_cancelada_e_descartado_sem_reabrir()
    {
        await DiagnosticadaAsync(9);
        await _oficina.CancelarAsync(9, "orçamento rejeitado");

        Assert.Equal(EResultadoDoEvento.Ignorado, await _oficina.LiberarAsync(9));
        Assert.Equal(EEtapaDeExecucao.Cancelada, (await _ordens.ObterPorOrdemAsync(9))!.Etapa);
    }

    [Fact]
    public async Task Finalizar_baixa_as_pecas_e_publica_uma_vez()
    {
        var filtro = await PecaAsync(estoque: 10);
        await DiagnosticadaAsync(10, MaoDeObra(), PecaPedida(filtro.Id, 3));
        await _oficina.LiberarAsync(10);

        var finalizada = await _oficina.FinalizarAsync(10, "corr-10");
        var deNovo = await _oficina.FinalizarAsync(10, "corr-10");

        Assert.Equal(EEtapaDeExecucao.Finalizada, deNovo.Etapa);
        Assert.Contains(finalizada.EventosPendentes, e => e.Tipo == "ExecucaoFinalizada");
        Assert.Single(deNovo.EventosPendentes, e => e.Tipo == "ExecucaoFinalizada");

        var peca = (await _pecas.ObterAsync(filtro.Id))!;
        Assert.Equal(7, peca.QuantidadeEmEstoque);
        Assert.Empty(peca.Reservas);
    }

    [Fact]
    public async Task Finalizar_sem_pagamento_nao_mexe_no_estoque()
    {
        var filtro = await PecaAsync(estoque: 10);
        await DiagnosticadaAsync(11, PecaPedida(filtro.Id, 3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _oficina.FinalizarAsync(11, "corr-11"));

        var peca = (await _pecas.ObterAsync(filtro.Id))!;
        Assert.Equal(10, peca.QuantidadeEmEstoque);
        Assert.Equal(3, peca.QuantidadeReservada);
    }

    [Fact]
    public async Task Finalizar_ordem_que_nao_existe_diz_qual()
    {
        await Assert.ThrowsAsync<OrdemNaoEncontradaException>(() => _oficina.FinalizarAsync(1100, "corr"));
    }

    [Fact]
    public async Task Cancelar_devolve_as_pecas_e_repetir_nao_muda_nada()
    {
        var filtro = await PecaAsync(estoque: 5);
        await DiagnosticadaAsync(12, PecaPedida(filtro.Id, 2));

        Assert.Equal(EResultadoDoEvento.Aplicado, await _oficina.CancelarAsync(12, "pagamento estornado"));
        Assert.Equal(EResultadoDoEvento.JaAplicado, await _oficina.CancelarAsync(12, "pagamento estornado"));

        var ordem = (await _ordens.ObterPorOrdemAsync(12))!;
        Assert.Equal(EEtapaDeExecucao.Cancelada, ordem.Etapa);
        Assert.Equal("pagamento estornado", ordem.MotivoDoCancelamento);

        var peca = (await _pecas.ObterAsync(filtro.Id))!;
        Assert.Equal(5, peca.Disponivel);
        Assert.Empty(peca.Reservas);
    }

    [Fact]
    public async Task Cancelamento_depois_do_trabalho_pronto_nao_devolve_o_que_ja_saiu()
    {
        var filtro = await PecaAsync(estoque: 5);
        await DiagnosticadaAsync(13, PecaPedida(filtro.Id, 2));
        await _oficina.LiberarAsync(13);
        await _oficina.FinalizarAsync(13, "corr-13");

        Assert.Equal(EResultadoDoEvento.Ignorado, await _oficina.CancelarAsync(13, "timeout"));

        Assert.Equal(EEtapaDeExecucao.Finalizada, (await _ordens.ObterPorOrdemAsync(13))!.Etapa);
        Assert.Equal(3, (await _pecas.ObterAsync(filtro.Id))!.QuantidadeEmEstoque);
    }

    [Fact]
    public async Task Cancelar_ordem_desconhecida_ainda_solta_reserva_perdida()
    {
        // Não deveria haver reserva de uma ordem que não está aqui. Se houver,
        // é resto de alguma falha, e prender a peça para sempre é pior.
        var filtro = await PecaAsync(estoque: 5);
        await _pecas.ReservarAsync(filtro.Id, 1400, 2, Agora);

        Assert.Equal(EResultadoDoEvento.OrdemDesconhecida, await _oficina.CancelarAsync(1400, "timeout"));

        Assert.Empty((await _pecas.ObterAsync(filtro.Id))!.Reservas);
    }

    [Fact]
    public async Task O_evento_do_diagnostico_leva_os_itens_e_o_cliente()
    {
        var filtro = await PecaAsync(preco: 38.90m);

        var ordem = await DiagnosticadaAsync(15, MaoDeObra(120.50m), PecaPedida(filtro.Id, 2));

        var evento = SerializacaoDeEvento.Desserializar<DiagnosticoConcluido>(
            Assert.Single(ordem.EventosPendentes).Payload)?.Dados;

        Assert.NotNull(evento);
        Assert.Equal("ana@oficina.test", evento.EmailCliente);
        Assert.Equal(198.30m, evento.ValorTotal);
        Assert.Contains(evento.Itens, i => i is { Tipo: ETipoItem.Peca, Quantidade: 2, ValorUnitario: 38.90m });
    }
}
