using System.Net;
using System.Net.Http.Json;
using PosTech.Contratos;
using PosTech.Execucao;

namespace PosTech.Execucao.Testes;

/// <summary>
/// Quem pode o quê nas rotas do Execução.
///
/// É o serviço da bancada: fila, diagnóstico, finalização e estoque são da
/// equipe. O cliente alcança uma coisa só — a leitura da própria ordem. Sem a
/// checagem, um cliente autenticado registrava diagnóstico e dava entrada de
/// estoque.
/// </summary>
[Collection("execucao")]
public class AcessoDaApiTests
{
    private const int Dono = 10;

    private readonly AmbienteDaExecucao _ambiente;

    public AcessoDaApiTests(AmbienteDaExecucao ambiente) => _ambiente = ambiente;

    private HttpClient Como(string? token) => _ambiente.Cliente().Como(token);

    private async Task AbrirAsync(int idOrdem)
    {
        await _ambiente.PublicarComoAsync(
            new OrdemAberta(idOrdem, Dono, "Ana Prado", "ana@oficina.test", 20, "Gol AAA-1234", 30, DateTime.UtcNow),
            "os-service", $"corr-{idOrdem}");

        await InfraDeTeste.AguardarAsync(
            async () => await _ambiente.OrdemAsync(idOrdem),
            $"a ordem {idOrdem} chegar na oficina");
    }

    private static object UmDiagnostico() => new
    {
        itens = new object[] { new { descricao = "Troca de óleo", tipo = "MaoDeObra", quantidade = 1, valorUnitario = 120.50m } },
    };

    private static object UmaPeca() => new
    {
        nome = "Filtro de óleo", marca = "Bosch", codigo = $"FO-{Guid.NewGuid():N}", preco = 38.90m, quantidadeEmEstoque = 5,
    };

    [Theory]
    [InlineData("/api/v1/execucao/fila")]
    [InlineData("/api/v1/execucao/ordens/1")]
    [InlineData("/api/v1/pecas")]
    public async Task Sem_token_nenhuma_rota_de_negocio_responde(string rota)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Como(null).GetAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Como(TokenDeTeste.Vencido()).GetAsync(rota)).StatusCode);
    }

    [Fact]
    public async Task O_health_continua_publico()
    {
        Assert.Equal(HttpStatusCode.OK, (await Como(null).GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_ve_a_fila_nem_o_estoque()
    {
        var http = Como(TokenDeTeste.Cliente(Dono));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/execucao/fila")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/pecas")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/v1/pecas", UmaPeca())).StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_registra_diagnostico_nem_finaliza_nem_na_propria_ordem()
    {
        await AbrirAsync(8101);
        var http = Como(TokenDeTeste.Cliente(Dono));

        var diagnostico = await http.PostAsJsonAsync("/api/v1/execucao/ordens/8101/diagnostico", UmDiagnostico());
        var finalizacao = await http.PostAsync("/api/v1/execucao/ordens/8101/finalizar", null);

        Assert.Equal(HttpStatusCode.Forbidden, diagnostico.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, finalizacao.StatusCode);
        Assert.Equal(EEtapaDeExecucao.AguardandoDiagnostico, (await _ambiente.OrdemAsync(8101))!.Etapa);
    }

    [Fact]
    public async Task O_cliente_le_a_propria_ordem_e_a_de_outro_responde_como_se_nao_existisse()
    {
        await AbrirAsync(8102);

        Assert.Equal(HttpStatusCode.OK, (await Como(TokenDeTeste.Cliente(Dono)).GetAsync("/api/v1/execucao/ordens/8102")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Como(TokenDeTeste.Mecanico()).GetAsync("/api/v1/execucao/ordens/8102")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Como(TokenDeTeste.Cliente(Dono + 1)).GetAsync("/api/v1/execucao/ordens/8102")).StatusCode);
    }

    [Fact]
    public async Task Qualquer_cargo_da_equipe_opera_a_bancada()
    {
        await AbrirAsync(8103);
        var http = Como(TokenDeTeste.Mecanico());

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/execucao/fila")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync("/api/v1/pecas", UmaPeca())).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await http.PostAsJsonAsync("/api/v1/execucao/ordens/8103/diagnostico", UmDiagnostico())).StatusCode);
    }
}
