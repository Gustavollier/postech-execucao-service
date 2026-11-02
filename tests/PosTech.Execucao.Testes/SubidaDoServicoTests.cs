using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;
using PosTech.Execucao;

namespace PosTech.Execucao.Testes;

/// <summary>
/// A API sobe, e sobe inteira.
///
/// Herdado da lição do Billing: lá o serviço compilava, os testes passavam, e
/// ele não subia — faltava uma dependência no contêiner de injeção. Montar o
/// grafo de dependências é comportamento do serviço e é exercitado como tal.
/// </summary>
[Collection("execucao")]
public class SubidaDoServicoTests
{
    private readonly AmbienteDaExecucao _ambiente;

    public SubidaDoServicoTests(AmbienteDaExecucao ambiente) => _ambiente = ambiente;

    [Fact]
    public async Task O_servico_sobe_com_todas_as_dependencias_resolvidas()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_readiness_responde_separado_do_liveness()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_swagger_e_publicado()
    {
        // É entregável da fase: o contrato da API tem que estar acessível.
        var resposta = await _ambiente.Cliente().GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("/api/v1/execucao/ordens/{idOrdem}/diagnostico", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Os_indices_sao_criados_na_partida()
    {
        // O CriadorDeIndices não derruba o serviço se falhar (de propósito),
        // então os índices que garantem regra precisam ser conferidos.
        var ordens = await NomesDosIndicesAsync(OrdemRepositorio.Colecao);
        var pecas = await NomesDosIndicesAsync(PecaRepositorio.Colecao);
        var idempotencia = await NomesDosIndicesAsync("eventosProcessados");

        Assert.Contains("ux_idOrdem", ordens);
        Assert.Contains("ix_eventosPendentes", ordens);
        Assert.Contains("ux_codigo", pecas);
        Assert.Contains("ix_reservas_idOrdem", pecas);
        Assert.Contains("ttl_processadoEm", idempotencia);
    }

    private async Task<List<string>> NomesDosIndicesAsync(string colecao)
    {
        var indices = await InfraDeTeste.AguardarAsync(async () =>
        {
            var lista = await (await _ambiente.Banco.GetCollection<object>(colecao).Indexes.ListAsync()).ToListAsync();
            return lista.Count > 1 ? lista : null;
        }, $"os índices de {colecao}");

        return indices.Select(i => i["name"].AsString).ToList();
    }

    [Theory]
    [InlineData("MongoConnectionString")]
    [InlineData("RabbitMqConnectionString")]
    public void Sem_conexao_obrigatoria_o_servico_se_recusa_a_subir(string faltando)
    {
        // Falhar na partida é melhor que descobrir na primeira mensagem.
        using var fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (faltando != "MongoConnectionString")
                builder.UseSetting("MongoConnectionString", "mongodb://localhost:1");

            if (faltando != "RabbitMqConnectionString")
                builder.UseSetting("RabbitMqConnectionString", "amqp://localhost:1");
        });

        var erro = Assert.ThrowsAny<Exception>(() => fabrica.CreateClient());

        Assert.Contains(faltando, erro.ToString());
    }
}

/// <summary>O registro de idempotência contra Mongo de verdade.</summary>
[Collection("execucao")]
public sealed class IdempotenciaMongoTests
{
    private readonly IMongoDatabase _banco;
    private readonly IdempotenciaMongo _idempotencia;

    public IdempotenciaMongoTests(AmbienteDaExecucao ambiente)
    {
        _banco = ambiente.BancoIsolado();
        _idempotencia = new IdempotenciaMongo(_banco);
        _idempotencia.GarantirIndicesAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Registrar_marca_a_mensagem_so_para_aquele_consumidor()
    {
        var mensagem = Guid.NewGuid();

        Assert.False(await _idempotencia.JaProcessadoAsync(mensagem, "execucao-service"));

        await _idempotencia.RegistrarAsync(mensagem, "OrdemCancelada", "execucao-service", DateTime.UtcNow);

        Assert.True(await _idempotencia.JaProcessadoAsync(mensagem, "execucao-service"));

        // O mesmo OrdemCancelada chega ao Billing e ao Execução, e um não pode
        // calar o outro.
        Assert.False(await _idempotencia.JaProcessadoAsync(mensagem, "billing-service"));
    }

    [Fact]
    public async Task Gravacoes_concorrentes_da_mesma_mensagem_nao_derrubam_o_consumidor()
    {
        var mensagem = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            _idempotencia.RegistrarAsync(mensagem, "OrdemAberta", "execucao-service", DateTime.UtcNow)));

        var registros = await _banco.GetCollection<EventoProcessado>("eventosProcessados")
            .CountDocumentsAsync(FilterDefinition<EventoProcessado>.Empty);

        Assert.Equal(1, registros);
    }
}
