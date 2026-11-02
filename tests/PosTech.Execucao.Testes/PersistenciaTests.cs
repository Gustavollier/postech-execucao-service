using MongoDB.Driver;
using PosTech.Contratos;
using PosTech.Execucao;
using PosTech.Mensageria;

namespace PosTech.Execucao.Testes;

/// <summary>
/// O estoque contra MongoDB de verdade.
///
/// A regra pura de <see cref="Estoque"/> já está testada; o que se prova aqui
/// é que o repositório aplica a mesma regra <b>atomicamente</b>. Dublê não
/// prova isso: num dicionário em memória, dez reservas em paralelo nunca
/// disputam o mesmo documento.
/// </summary>
[Collection("execucao")]
public class PecaRepositorioTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly PecaRepositorio _pecas;

    public PecaRepositorioTests(AmbienteDaExecucao ambiente)
    {
        _pecas = new PecaRepositorio(ambiente.BancoIsolado());
        _pecas.GarantirIndicesAsync().GetAwaiter().GetResult();
    }

    private async Task<Peca> NovaAsync(int estoque = 10, decimal preco = 38.90m, bool ativa = true)
    {
        var peca = new Peca
        {
            Nome = "Filtro de óleo", Marca = "Bosch", Codigo = $"FO-{Guid.NewGuid():N}",
            Preco = preco, QuantidadeEmEstoque = estoque, Ativo = ativa,
        };

        Assert.True(await _pecas.InserirAsync(peca));
        return peca;
    }

    [Fact]
    public async Task Reservas_simultaneas_nao_vendem_a_mesma_peca_duas_vezes()
    {
        // Dez mecânicos diagnosticando ao mesmo tempo, cinco peças na
        // prateleira. Com "ler, mudar, gravar", vários leriam saldo 5 e todos
        // reservariam. Com a condição dentro da escrita, exatamente cinco
        // passam.
        var peca = await NovaAsync(estoque: 5);

        var tentativas = Enumerable.Range(1, 10).Select(async idOrdem =>
        {
            try
            {
                return await _pecas.ReservarAsync(peca.Id, idOrdem, 1, Agora);
            }
            catch (EstoqueInsuficienteException)
            {
                return false;
            }
        });

        var resultados = await Task.WhenAll(tentativas);

        Assert.Equal(5, resultados.Count(r => r));

        var final = await _pecas.ObterAsync(peca.Id);
        Assert.NotNull(final);
        Assert.Equal(5, final.QuantidadeReservada);
        Assert.Equal(0, final.Disponivel);
        Assert.Equal(5, final.Reservas.Select(r => r.IdOrdem).Distinct().Count());
    }

    [Fact]
    public async Task Reservar_de_novo_para_a_mesma_ordem_nao_soma()
    {
        var peca = await NovaAsync();

        Assert.True(await _pecas.ReservarAsync(peca.Id, 1, 3, Agora));
        Assert.False(await _pecas.ReservarAsync(peca.Id, 1, 3, Agora));

        Assert.Equal(3, (await _pecas.ObterAsync(peca.Id))!.QuantidadeReservada);
    }

    [Fact]
    public async Task Sem_saldo_a_reserva_falha_com_os_numeros()
    {
        var peca = await NovaAsync(estoque: 2);

        var falta = await Assert.ThrowsAsync<EstoqueInsuficienteException>(
            () => _pecas.ReservarAsync(peca.Id, 1, 3, Agora));

        Assert.Equal(peca.Id, falta.IdPeca);
        Assert.Equal(2, falta.Disponivel);
        Assert.Empty((await _pecas.ObterAsync(peca.Id))!.Reservas);
    }

    [Fact]
    public async Task Peca_inativa_nao_e_reservada()
    {
        var peca = await NovaAsync(ativa: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _pecas.ReservarAsync(peca.Id, 1, 1, Agora));
    }

    [Fact]
    public async Task Peca_que_nao_existe_e_dita_pelo_nome()
    {
        var erro = await Assert.ThrowsAsync<PecaNaoEncontradaException>(
            () => _pecas.ReservarAsync("nao-existe", 1, 1, Agora));

        Assert.Equal("nao-existe", erro.IdPeca);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Quantidade_nao_positiva_nao_chega_no_banco(int quantidade)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _pecas.ReservarAsync("qualquer", 1, quantidade, Agora));
    }

    [Fact]
    public async Task Baixar_tira_so_a_reserva_da_ordem_e_uma_vez_so()
    {
        var peca = await NovaAsync(estoque: 10);
        await _pecas.ReservarAsync(peca.Id, idOrdem: 1, quantidade: 3, Agora);
        await _pecas.ReservarAsync(peca.Id, idOrdem: 2, quantidade: 4, Agora);

        Assert.True(await _pecas.BaixarAsync(peca.Id, 1));
        Assert.False(await _pecas.BaixarAsync(peca.Id, 1));

        var final = (await _pecas.ObterAsync(peca.Id))!;
        Assert.Equal(7, final.QuantidadeEmEstoque);
        Assert.Equal(2, Assert.Single(final.Reservas).IdOrdem);
        Assert.Equal(3, final.Disponivel);
    }

    [Fact]
    public async Task Devolver_repetido_nao_toca_na_reserva_de_outra_ordem()
    {
        var peca = await NovaAsync(estoque: 4);
        await _pecas.ReservarAsync(peca.Id, 1, 2, Agora);
        await _pecas.ReservarAsync(peca.Id, 2, 2, Agora);

        Assert.True(await _pecas.DevolverAsync(peca.Id, 1));
        Assert.False(await _pecas.DevolverAsync(peca.Id, 1));

        var final = (await _pecas.ObterAsync(peca.Id))!;
        Assert.Equal(2, Assert.Single(final.Reservas).IdOrdem);
    }

    [Fact]
    public async Task Devolver_todas_da_ordem_limpa_em_qualquer_peca()
    {
        var filtro = await NovaAsync();
        var pastilha = await NovaAsync();
        await _pecas.ReservarAsync(filtro.Id, 7, 1, Agora);
        await _pecas.ReservarAsync(pastilha.Id, 7, 2, Agora);
        await _pecas.ReservarAsync(pastilha.Id, 8, 1, Agora);

        Assert.Equal(2, await _pecas.DevolverTodasDaOrdemAsync(7));
        Assert.Equal(0, await _pecas.DevolverTodasDaOrdemAsync(7));

        Assert.Empty((await _pecas.ObterAsync(filtro.Id))!.Reservas);
        Assert.Equal(8, Assert.Single((await _pecas.ObterAsync(pastilha.Id))!.Reservas).IdOrdem);
    }

    [Fact]
    public async Task Codigo_repetido_nao_entra()
    {
        var peca = await NovaAsync();

        var copia = new Peca { Nome = "Outro", Codigo = peca.Codigo, Preco = 1m };

        Assert.False(await _pecas.InserirAsync(copia));
    }

    [Fact]
    public async Task Entrada_soma_ao_estoque_e_peca_inexistente_devolve_nulo()
    {
        var peca = await NovaAsync(estoque: 2);

        var depois = await _pecas.DarEntradaAsync(peca.Id, 5);

        Assert.Equal(7, depois!.QuantidadeEmEstoque);
        Assert.Null(await _pecas.DarEntradaAsync("nao-existe", 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _pecas.DarEntradaAsync(peca.Id, 0));
    }

    [Fact]
    public async Task Listar_esconde_as_inativas_a_menos_que_se_peca()
    {
        var ativa = await NovaAsync();
        var inativa = await NovaAsync(ativa: false);

        var ativas = await _pecas.ListarAsync();
        var todas = await _pecas.ListarAsync(apenasAtivas: false);

        Assert.Contains(ativas, p => p.Id == ativa.Id);
        Assert.DoesNotContain(ativas, p => p.Id == inativa.Id);
        Assert.Contains(todas, p => p.Id == inativa.Id);
    }

    [Fact]
    public async Task O_preco_volta_do_banco_com_os_centavos()
    {
        // Decimal128, não double: 0.1 + 0.2 tem que continuar sendo 0.3 no
        // orçamento.
        var peca = await NovaAsync(preco: 1234.57m);

        Assert.Equal(1234.57m, (await _pecas.ObterAsync(peca.Id))!.Preco);
    }
}

/// <summary>A ordem e o outbox embutido, contra MongoDB de verdade.</summary>
[Collection("execucao")]
public class OrdemRepositorioTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IMongoDatabase _banco;
    private readonly OrdemRepositorio _ordens;

    public OrdemRepositorioTests(AmbienteDaExecucao ambiente)
    {
        _banco = ambiente.BancoIsolado();
        _ordens = new OrdemRepositorio(_banco);
        _ordens.GarantirIndicesAsync().GetAwaiter().GetResult();
    }

    private static OrdemEmExecucao Ordem(int idOrdem, DateTime? abertaEm = null) =>
        GestorDaExecucao.Receber(
            new OrdemAberta(idOrdem, 10, "Ana Prado", "ana@oficina.test", 20, "Gol AAA-1234", 30, abertaEm ?? Agora),
            Agora);

    private static ItemLancado MaoDeObra() =>
        new() { Descricao = "Troca de óleo", Tipo = ETipoItem.MaoDeObra, Quantidade = 1, ValorUnitario = 120.50m };

    [Fact]
    public async Task A_mesma_ordem_nao_entra_duas_vezes()
    {
        Assert.True(await _ordens.InserirAsync(Ordem(1)));
        Assert.False(await _ordens.InserirAsync(Ordem(1)));

        Assert.Equal(1, await _banco.GetCollection<OrdemEmExecucao>(OrdemRepositorio.Colecao)
            .CountDocumentsAsync(o => o.IdOrdem == 1));
    }

    [Fact]
    public async Task Gravar_sobre_uma_versao_velha_falha_e_nao_escreve_nada()
    {
        // Os dois lados leem a mesma versão — é o clique duplo no diagnóstico.
        await _ordens.InserirAsync(Ordem(2));

        var primeiro = (await _ordens.ObterPorOrdemAsync(2))!;
        var segundo = (await _ordens.ObterPorOrdemAsync(2))!;

        GestorDaExecucao.ConcluirDiagnostico(primeiro, [MaoDeObra()], "corr-a", Agora);
        await _ordens.SalvarAsync(primeiro);

        GestorDaExecucao.ConcluirDiagnostico(segundo, [MaoDeObra(), MaoDeObra()], "corr-b", Agora);

        var conflito = await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => _ordens.SalvarAsync(segundo));
        Assert.Equal(2, conflito.IdOrdem);

        var gravada = (await _ordens.ObterPorOrdemAsync(2))!;
        Assert.Single(gravada.Itens);
        Assert.Equal("corr-a", Assert.Single(gravada.EventosPendentes).CorrelationId);
    }

    [Fact]
    public async Task A_fila_vem_na_ordem_de_atendimento_e_sem_as_encerradas()
    {
        var naBancada = Ordem(10, Agora.AddHours(3));
        var esperandoDiagnosticoAntiga = Ordem(11, Agora.AddHours(1));
        var esperandoDiagnosticoNova = Ordem(12, Agora.AddHours(2));
        var esperandoPagamento = Ordem(13, Agora);
        var cancelada = Ordem(14, Agora);

        GestorDaExecucao.ConcluirDiagnostico(naBancada, [MaoDeObra()], "c", Agora);
        GestorDaExecucao.Liberar(naBancada, Agora);
        GestorDaExecucao.ConcluirDiagnostico(esperandoPagamento, [MaoDeObra()], "c", Agora);
        GestorDaExecucao.Cancelar(cancelada, "desistiu", Agora);

        foreach (var ordem in new[] { esperandoPagamento, esperandoDiagnosticoNova, cancelada, naBancada, esperandoDiagnosticoAntiga })
            await _ordens.InserirAsync(ordem);

        var fila = await _ordens.ListarFilaAsync();

        Assert.Equal([10, 11, 12, 13], fila.Select(o => o.IdOrdem));

        var soDiagnostico = await _ordens.ListarFilaAsync(EEtapaDeExecucao.AguardandoDiagnostico);
        Assert.Equal([11, 12], soDiagnostico.Select(o => o.IdOrdem));

        var soCanceladas = await _ordens.ListarFilaAsync(EEtapaDeExecucao.Cancelada);
        Assert.Equal(14, Assert.Single(soCanceladas).IdOrdem);
    }

    [Fact]
    public async Task O_outbox_le_os_eventos_embutidos_e_remove_o_publicado()
    {
        var ordem = Ordem(20);
        await _ordens.InserirAsync(ordem);
        GestorDaExecucao.ConcluirDiagnostico(ordem, [MaoDeObra()], "corr-20", Agora);
        await _ordens.SalvarAsync(ordem);

        var outbox = new OutboxMongo(_banco);

        var pendente = Assert.Single(await outbox.ObterPendentesAsync(10));
        Assert.Equal("DiagnosticoConcluido", pendente.Tipo);

        await outbox.MarcarPublicadoAsync(pendente.MensagemId, DateTime.UtcNow);

        Assert.Empty(await outbox.ObterPendentesAsync(10));
        Assert.Empty((await _ordens.ObterPorOrdemAsync(20))!.EventosPendentes);
    }

    [Fact]
    public async Task Falha_de_publicacao_fica_registrada_no_proprio_evento()
    {
        var ordem = Ordem(21);
        await _ordens.InserirAsync(ordem);
        GestorDaExecucao.ConcluirDiagnostico(ordem, [MaoDeObra()], "corr-21", Agora);
        await _ordens.SalvarAsync(ordem);

        var outbox = new OutboxMongo(_banco);
        var pendente = Assert.Single(await outbox.ObterPendentesAsync(10));

        var depois = DateTime.UtcNow.AddMinutes(5);
        await outbox.RegistrarFalhaAsync(pendente.MensagemId, 1, depois, "broker fora", EStatusOutbox.Pendente);

        // Fora da janela de nova tentativa, não aparece para o publicador.
        Assert.Empty(await outbox.ObterPendentesAsync(10));

        var evento = Assert.Single((await _ordens.ObterPorOrdemAsync(21))!.EventosPendentes);
        Assert.Equal(1, evento.Tentativas);
        Assert.Equal("broker fora", evento.UltimoErro);
    }

    [Fact]
    public async Task O_ProcessadorDoOutbox_funciona_igual_sobre_o_Mongo()
    {
        // O mesmo processador que roda sobre Azure SQL no OS Service. Ele não
        // sabe onde o dado mora — e este teste prova que não precisa saber.
        var ordem = Ordem(22);
        await _ordens.InserirAsync(ordem);
        GestorDaExecucao.ConcluirDiagnostico(ordem, [MaoDeObra()], "corr-22", Agora);
        await _ordens.SalvarAsync(ordem);

        var publicador = new PublicadorFalso();
        var processador = new ProcessadorDoOutbox(new OutboxMongo(_banco), publicador);

        Assert.Equal(1, await processador.ProcessarLoteAsync());
        Assert.Equal("DiagnosticoConcluido", Assert.Single(publicador.Publicados).Tipo);
        Assert.Empty((await _ordens.ObterPorOrdemAsync(22))!.EventosPendentes);
    }
}
