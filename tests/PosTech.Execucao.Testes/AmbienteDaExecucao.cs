using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;
using PosTech.Contratos;
using PosTech.Execucao;
using PosTech.Mensageria;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace PosTech.Execucao.Testes;

/// <summary>
/// A API do Execução no ar, com MongoDB e RabbitMQ de verdade.
///
/// É <c>ICollectionFixture</c> e não <c>IAsyncLifetime</c> na classe de teste
/// porque o xUnit instancia a classe uma vez por teste: cada <c>[Fact]</c>
/// subiria o seu par de contêineres. Aqui sobem uma vez e todos os testes da
/// coleção usam o mesmo ambiente, que é também o que evita dois consumidores
/// disputando a mesma fila.
///
/// Nada é dublê. O Execução não fala com nenhum sistema de fora do nosso, então
/// não há o que substituir.
/// </summary>
public sealed class AmbienteDaExecucao : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = InfraDeTeste.Mongo();
    private readonly RabbitMqContainer _rabbit = InfraDeTeste.Rabbit();

    private MongoClient _cliente = null!;

    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    /// <summary>O banco que a API usa.</summary>
    public IMongoDatabase Banco { get; private set; } = null!;

    public string RabbitConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Um banco novo, vazio, no mesmo contêiner. Para os testes de repositório
    /// e de caso de uso, que precisam de estado previsível e não podem ver o
    /// que a API e os outros testes gravaram.
    /// </summary>
    public IMongoDatabase BancoIsolado() => _cliente.GetDatabase($"teste-{Guid.NewGuid():N}");

    /// <summary>
    /// Um cliente HTTP. Com <paramref name="correlationId"/>, manda o
    /// <c>X-Correlation-ID</c> em toda chamada — que é como o APIM chama a API
    /// em produção, e é de onde os controllers tiram a correlação que vai no
    /// evento publicado.
    /// </summary>
    public HttpClient Cliente(string? correlationId = null)
    {
        var cliente = Fabrica.CreateClient();

        if (correlationId is { Length: > 0 })
            cliente.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

        return cliente;
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mongo.StartAsync(), _rabbit.StartAsync());

        RabbitConnectionString = _rabbit.GetConnectionString();
        _cliente = new MongoClient(_mongo.GetConnectionString());
        Banco = _cliente.GetDatabase("execucao-db");

        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MongoConnectionString", _mongo.GetConnectionString());
            builder.UseSetting("RabbitMqConnectionString", RabbitConnectionString);
            builder.UseSetting("MongoDatabase", "execucao-db");
        });

        // Força a subida do host: é o CreateClient que constrói o grafo de
        // dependências e inicia os serviços de fundo.
        _ = Fabrica.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await Task.WhenAll(_mongo.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Publica um evento no exchange como outro serviço faria, passando pelo
    /// mesmo envelope e pela mesma routing key da produção.
    /// </summary>
    public async Task PublicarComoAsync<T>(T evento, string origem, string correlationId)
        where T : notnull
    {
        await using var conexao = new ConexaoRabbitMq(RabbitConnectionString, origem);

        await conexao.DeclararTopologiaAsync();

        var publicador = new PublicadorRabbitMq(conexao);

        await publicador.PublicarAsync(Outbox.Enfileirar(evento, correlationId, origem));
    }

    /// <summary>Lê a ordem direto do Mongo, sem passar pela API.</summary>
    public Task<OrdemEmExecucao?> OrdemAsync(int idOrdem) =>
        new OrdemRepositorio(Banco).ObterPorOrdemAsync(idOrdem);

    public Task<Peca?> PecaAsync(string id) => new PecaRepositorio(Banco).ObterAsync(id);
}

[CollectionDefinition("execucao")]
public sealed class ColecaoDaExecucao : ICollectionFixture<AmbienteDaExecucao>;

/// <summary>
/// Escuta uma fila como se fosse o serviço dono dela. É o observador que prova
/// que o evento saiu de verdade pelo broker, e não só foi gravado no
/// documento: o <c>DiagnosticoConcluido</c> tem que chegar na fila do Billing,
/// e o <c>ExecucaoFinalizada</c> na do orquestrador.
/// </summary>
public sealed class EspiaoDaFila : IAsyncDisposable
{
    private readonly ConexaoRabbitMq _conexao;
    private readonly ConsumidorDeEventos _consumidor;
    private readonly List<EnvelopeRecebido> _recebidos = [];
    private readonly object _trava = new();

    private EspiaoDaFila(ConexaoRabbitMq conexao, ConsumidorDeEventos consumidor)
    {
        _conexao = conexao;
        _consumidor = consumidor;
    }

    public static async Task<EspiaoDaFila> LigarAsync(string connectionString, string fila, params string[] tipos)
    {
        var conexao = new ConexaoRabbitMq(connectionString, "espiao");

        await conexao.DeclararTopologiaAsync();

        EspiaoDaFila? espiao = null;

        var tratadores = tipos.ToDictionary(
            tipo => tipo,
            _ => (TratadorDeEvento)((envelope, _) =>
            {
                espiao!.Guardar(envelope);
                return Task.CompletedTask;
            }));

        var consumidor = new ConsumidorDeEventos(
            conexao, fila, "espiao", new IdempotenciaEmMemoria(), tratadores);

        espiao = new EspiaoDaFila(conexao, consumidor);

        await consumidor.IniciarAsync();

        return espiao;
    }

    private void Guardar(EnvelopeRecebido envelope)
    {
        lock (_trava)
        {
            _recebidos.Add(envelope);
        }
    }

    public IReadOnlyList<EnvelopeRecebido> Recebidos
    {
        get
        {
            lock (_trava)
            {
                return [.. _recebidos];
            }
        }
    }

    /// <summary>
    /// Espera um evento chegar, filtrando pelo <c>correlationId</c>.
    ///
    /// O filtro não é luxo: as filas espiadas não são drenadas por ninguém
    /// entre um teste e outro, então acumulam o que os anteriores produziram.
    /// Procurar só pelo tipo acha o evento de outra ordem e a asserção passa ou
    /// falha por acidente — foi exatamente o que aconteceu na primeira rodada.
    /// </summary>
    public Task<EnvelopeRecebido> EsperarAsync(string tipo, string correlationId, int segundos = 45) =>
        InfraDeTeste.AguardarAsync(
            () => Task.FromResult(
                Recebidos.FirstOrDefault(e => e.Tipo == tipo && e.CorrelationId == correlationId)),
            $"o evento {tipo} de {correlationId} chegar na fila",
            segundos);

    public async ValueTask DisposeAsync()
    {
        await _consumidor.DisposeAsync();
        await _conexao.DisposeAsync();
    }
}
