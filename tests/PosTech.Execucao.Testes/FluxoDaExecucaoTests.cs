using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PosTech.Contratos;
using PosTech.Execucao;

namespace PosTech.Execucao.Testes;

/// <summary>
/// O Execução inteiro, do jeito que roda: a ordem entra pelo RabbitMQ, o
/// mecânico trabalha por HTTP, e os eventos de saída atravessam o broker até
/// as filas de quem consome — o <c>DiagnosticoConcluido</c> até o Billing, o
/// <c>ExecucaoFinalizada</c> até o orquestrador.
///
/// Nada é dublê. É na infraestrutura de verdade que mora o que dublê não pega:
/// routing key trocada, fila que não assina o evento, evento preso no outbox.
/// </summary>
[Collection("execucao")]
public class FluxoDaExecucaoTests
{
    private readonly AmbienteDaExecucao _ambiente;

    public FluxoDaExecucaoTests(AmbienteDaExecucao ambiente) => _ambiente = ambiente;

    private static OrdemAberta Aberta(int idOrdem) =>
        new(idOrdem, 10, "Ana Prado", "ana@oficina.test", 20, "Gol AAA-1234", 30, DateTime.UtcNow);

    private Task<OrdemEmExecucao> EsperarOrdemAsync(int idOrdem, EEtapaDeExecucao etapa) =>
        InfraDeTeste.AguardarAsync(
            async () => await _ambiente.OrdemAsync(idOrdem) is { } ordem && ordem.Etapa == etapa ? ordem : null,
            $"a ordem {idOrdem} chegar em {etapa}");

    private async Task<string> CadastrarPecaAsync(int estoque, decimal preco = 38.90m)
    {
        var resposta = await _ambiente.Cliente().PostAsJsonAsync("/api/v1/pecas", new
        {
            nome = "Filtro de óleo", marca = "Bosch", codigo = $"FO-{Guid.NewGuid():N}",
            preco, quantidadeEmEstoque = estoque,
        });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        return corpo.GetProperty("id").GetString()!;
    }

    private async Task AbrirAsync(int idOrdem, string correlationId)
    {
        await _ambiente.PublicarComoAsync(Aberta(idOrdem), "os-service", correlationId);
        await EsperarOrdemAsync(idOrdem, EEtapaDeExecucao.AguardandoDiagnostico);
    }

    private static object Diagnostico(string idPeca, int quantidade) => new
    {
        itens = new object[]
        {
            new { descricao = "Troca de óleo", tipo = "MaoDeObra", quantidade = 1, valorUnitario = 120.50m },
            new { tipo = "Peca", idPeca, quantidade },
        },
    };

    [Fact]
    public async Task A_ordem_atravessa_a_oficina_e_os_eventos_chegam_em_quem_consome()
    {
        // Os passos 2 e 6 da saga, sem atalho: abertura pela fila, diagnóstico
        // e finalização por HTTP, pagamento pela fila, e cada evento de saída
        // conferido na fila de destino com o correlationId intacto.
        await using var billing = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaBilling, "DiagnosticoConcluido");
        await using var orquestrador = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "ExecucaoFinalizada");

        var idPeca = await CadastrarPecaAsync(estoque: 10);

        await AbrirAsync(5001, "corr-5001");

        var diagnostico = await _ambiente.Cliente("corr-5001")
            .PostAsJsonAsync("/api/v1/execucao/ordens/5001/diagnostico", Diagnostico(idPeca, 2));

        Assert.Equal(HttpStatusCode.OK, diagnostico.StatusCode);

        var concluido = (await billing.EsperarAsync("DiagnosticoConcluido", "corr-5001")).Ler<DiagnosticoConcluido>();

        Assert.NotNull(concluido);
        Assert.Equal(198.30m, concluido.ValorTotal);
        Assert.Equal("ana@oficina.test", concluido.EmailCliente);
        Assert.Equal(2, concluido.Itens.Count);

        await _ambiente.PublicarComoAsync(
            new PagamentoConfirmado(5001, "pag-1", 198.30m, "mp-1", DateTime.UtcNow), "billing-service", "corr-5001");

        await EsperarOrdemAsync(5001, EEtapaDeExecucao.EmExecucao);

        var finalizar = await _ambiente.Cliente("corr-5001").PostAsync("/api/v1/execucao/ordens/5001/finalizar", null);
        Assert.Equal(HttpStatusCode.OK, finalizar.StatusCode);

        var finalizada = (await orquestrador.EsperarAsync("ExecucaoFinalizada", "corr-5001")).Ler<ExecucaoFinalizada>();
        Assert.Equal(5001, finalizada!.IdOrdem);

        var peca = (await _ambiente.PecaAsync(idPeca))!;
        Assert.Equal(8, peca.QuantidadeEmEstoque);
        Assert.Empty(peca.Reservas);
    }

    [Fact]
    public async Task A_compensacao_pela_fila_devolve_as_pecas()
    {
        // OrdemCancelada é como o orquestrador desfaz o passo do Execução:
        // orçamento rejeitado ou execução que não assumiu no prazo.
        var idPeca = await CadastrarPecaAsync(estoque: 3);

        await AbrirAsync(5002, "corr-5002");

        var diagnostico = await _ambiente.Cliente("corr-5002")
            .PostAsJsonAsync("/api/v1/execucao/ordens/5002/diagnostico", Diagnostico(idPeca, 3));
        Assert.Equal(HttpStatusCode.OK, diagnostico.StatusCode);

        Assert.Equal(0, (await _ambiente.PecaAsync(idPeca))!.Disponivel);

        await _ambiente.PublicarComoAsync(
            new OrdemCancelada(5002, "Orçamento rejeitado pelo cliente", DateTime.UtcNow), "os-service", "corr-5002");

        var cancelada = await EsperarOrdemAsync(5002, EEtapaDeExecucao.Cancelada);
        Assert.Equal("Orçamento rejeitado pelo cliente", cancelada.MotivoDoCancelamento);

        var peca = await InfraDeTeste.AguardarAsync(
            async () => await _ambiente.PecaAsync(idPeca) is { Reservas.Count: 0 } p ? p : null,
            "a reserva voltar para o estoque");

        Assert.Equal(3, peca.Disponivel);
    }

    [Fact]
    public async Task A_mesma_abertura_entregue_duas_vezes_entra_uma_vez_na_fila()
    {
        await _ambiente.PublicarComoAsync(Aberta(5003), "os-service", "corr-5003");
        await _ambiente.PublicarComoAsync(Aberta(5003), "os-service", "corr-5003");

        await EsperarOrdemAsync(5003, EEtapaDeExecucao.AguardandoDiagnostico);

        var fila = await _ambiente.Cliente().GetFromJsonAsync<JsonElement>("/api/v1/execucao/fila?etapa=AguardandoDiagnostico");

        Assert.Single(fila.EnumerateArray(), o => o.GetProperty("idOrdem").GetInt32() == 5003);
    }

    [Fact]
    public async Task A_fila_e_a_ordem_saem_com_a_etapa_por_nome()
    {
        await AbrirAsync(5004, "corr-5004");

        var ordem = await _ambiente.Cliente().GetFromJsonAsync<JsonElement>("/api/v1/execucao/ordens/5004");

        Assert.Equal("AguardandoDiagnostico", ordem.GetProperty("etapa").GetString());
        Assert.Equal("Gol AAA-1234", ordem.GetProperty("descricaoVeiculo").GetString());

        var fila = await _ambiente.Cliente().GetFromJsonAsync<JsonElement>("/api/v1/execucao/fila");
        Assert.Contains(fila.EnumerateArray(), o => o.GetProperty("idOrdem").GetInt32() == 5004);
    }

    [Fact]
    public async Task Sem_estoque_o_diagnostico_devolve_409_com_os_numeros()
    {
        var idPeca = await CadastrarPecaAsync(estoque: 1);
        await AbrirAsync(5005, "corr-5005");

        var resposta = await _ambiente.Cliente()
            .PostAsJsonAsync("/api/v1/execucao/ordens/5005/diagnostico", Diagnostico(idPeca, 2));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);

        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, problema.GetProperty("disponivel").GetInt32());
        Assert.Equal(2, problema.GetProperty("pedido").GetInt32());
        Assert.Equal(idPeca, problema.GetProperty("idPeca").GetString());
    }

    [Fact]
    public async Task Os_erros_do_diagnostico_tem_o_status_certo()
    {
        await AbrirAsync(5006, "corr-5006");
        var cliente = _ambiente.Cliente();

        var semOrdem = await cliente.PostAsJsonAsync("/api/v1/execucao/ordens/9999/diagnostico", Diagnostico("x", 1));
        Assert.Equal(HttpStatusCode.NotFound, semOrdem.StatusCode);

        var semItens = await cliente.PostAsJsonAsync("/api/v1/execucao/ordens/5006/diagnostico", new { itens = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, semItens.StatusCode);

        var semPeca = await cliente.PostAsJsonAsync("/api/v1/execucao/ordens/5006/diagnostico", Diagnostico("nao-existe", 1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, semPeca.StatusCode);

        var finalizarSemPagar = await cliente.PostAsync("/api/v1/execucao/ordens/5006/finalizar", null);
        Assert.Equal(HttpStatusCode.Conflict, finalizarSemPagar.StatusCode);

        var finalizarInexistente = await cliente.PostAsync("/api/v1/execucao/ordens/9999/finalizar", null);
        Assert.Equal(HttpStatusCode.NotFound, finalizarInexistente.StatusCode);

        var ordemInexistente = await cliente.GetAsync("/api/v1/execucao/ordens/9999");
        Assert.Equal(HttpStatusCode.NotFound, ordemInexistente.StatusCode);
    }

    [Fact]
    public async Task O_catalogo_valida_e_soma_entrada()
    {
        var cliente = _ambiente.Cliente();
        var idPeca = await CadastrarPecaAsync(estoque: 2);

        var semNome = await cliente.PostAsJsonAsync("/api/v1/pecas", new { codigo = "X", preco = 1m, quantidadeEmEstoque = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, semNome.StatusCode);

        var negativo = await cliente.PostAsJsonAsync("/api/v1/pecas", new { nome = "X", codigo = "X", preco = -1m, quantidadeEmEstoque = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, negativo.StatusCode);

        var peca = await cliente.GetFromJsonAsync<JsonElement>($"/api/v1/pecas/{idPeca}");
        var duplicada = await cliente.PostAsJsonAsync("/api/v1/pecas", new
        {
            nome = "Outra", codigo = peca.GetProperty("codigo").GetString(), preco = 1m, quantidadeEmEstoque = 1,
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);

        var entrada = await cliente.PostAsJsonAsync($"/api/v1/pecas/{idPeca}/entrada", new { quantidade = 5 });
        Assert.Equal(7, (await entrada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quantidadeEmEstoque").GetInt32());

        var entradaZero = await cliente.PostAsJsonAsync($"/api/v1/pecas/{idPeca}/entrada", new { quantidade = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, entradaZero.StatusCode);

        var entradaInexistente = await cliente.PostAsJsonAsync("/api/v1/pecas/nao-existe/entrada", new { quantidade = 1 });
        Assert.Equal(HttpStatusCode.NotFound, entradaInexistente.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/v1/pecas/nao-existe")).StatusCode);

        var catalogo = await cliente.GetFromJsonAsync<JsonElement>("/api/v1/pecas?incluirInativas=true");
        Assert.Contains(catalogo.EnumerateArray(), p => p.GetProperty("id").GetString() == idPeca);
    }
}
