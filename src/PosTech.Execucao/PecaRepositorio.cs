using MongoDB.Bson;
using MongoDB.Driver;

namespace PosTech.Execucao;

public interface IPecaRepositorio
{
    Task<Peca?> ObterAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Peca>> ListarAsync(bool apenasAtivas = true, CancellationToken cancellationToken = default);

    /// <summary>Grava uma peça nova. Devolve <c>false</c> se o código já existe.</summary>
    Task<bool> InserirAsync(Peca peca, CancellationToken cancellationToken = default);

    /// <summary>Soma ao estoque. Devolve a peça atualizada, ou <c>null</c> se não existe.</summary>
    Task<Peca?> DarEntradaAsync(string id, int quantidade, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserva para a ordem, de forma atômica. Devolve <c>true</c> se reservou
    /// agora e <c>false</c> se a ordem já tinha reserva nesta peça.
    /// Lança <see cref="EstoqueInsuficienteException"/>,
    /// <see cref="InvalidOperationException"/> (peça inativa) ou
    /// <see cref="PecaNaoEncontradaException"/>.
    /// </summary>
    Task<bool> ReservarAsync(string id, int idOrdem, int quantidade, DateTime agora, CancellationToken cancellationToken = default);

    /// <summary>Devolve a reserva da ordem. <c>false</c> se não havia.</summary>
    Task<bool> DevolverAsync(string id, int idOrdem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devolve tudo que a ordem tiver reservado, em qualquer peça. É o que a
    /// compensação usa: não depende dos itens que a ordem diz ter, então
    /// limpa também reserva que tenha ficado para trás de uma tentativa de
    /// diagnóstico interrompida no meio.
    /// </summary>
    Task<long> DevolverTodasDaOrdemAsync(int idOrdem, CancellationToken cancellationToken = default);

    /// <summary>Transforma a reserva da ordem em baixa. <c>false</c> se não havia.</summary>
    Task<bool> BaixarAsync(string id, int idOrdem, CancellationToken cancellationToken = default);
}

public sealed class PecaNaoEncontradaException(string idPeca)
    : Exception($"A peça '{idPeca}' não existe.")
{
    public string IdPeca { get; } = idPeca;
}

/// <summary>
/// O estoque em MongoDB.
///
/// As regras são as de <see cref="Estoque"/>, mas aqui elas não podem ser
/// "ler, mudar em memória, gravar": dois mecânicos diagnosticando ao mesmo
/// tempo leriam o mesmo saldo e os dois reservariam a última peça. Cada
/// operação vira uma escrita só, com a condição dentro do filtro — o Mongo
/// avalia e aplica atomicamente no documento, e quem perde a corrida
/// simplesmente não casa com o filtro.
/// </summary>
public sealed class PecaRepositorio : IPecaRepositorio
{
    public const string Colecao = "pecas";

    /// <summary>
    /// Quantas vezes reavaliar uma reserva que não casou com o filtro. Não
    /// casar pode ser corrida com uma entrada de estoque no meio; mais que
    /// isso, é falta de peça de verdade e a regra pura diz qual.
    /// </summary>
    private const int TentativasDeReserva = 3;

    private readonly IMongoCollection<Peca> _colecao;

    public PecaRepositorio(IMongoDatabase banco)
    {
        ArgumentNullException.ThrowIfNull(banco);

        _colecao = banco.GetCollection<Peca>(Colecao);
    }

    public async Task GarantirIndicesAsync(CancellationToken cancellationToken = default)
    {
        await _colecao.Indexes.CreateManyAsync(
            [
                // Código de catálogo é o que o balcão digita: duplicado vira
                // duas peças com o mesmo nome e saldos divergentes.
                new CreateIndexModel<Peca>(
                    Builders<Peca>.IndexKeys.Ascending(p => p.Codigo),
                    new CreateIndexOptions { Unique = true, Name = "ux_codigo" }),

                // Cancelamento e baixa procuram as peças pela reserva da ordem.
                new CreateIndexModel<Peca>(
                    Builders<Peca>.IndexKeys.Ascending("Reservas.IdOrdem"),
                    new CreateIndexOptions { Name = "ix_reservas_idOrdem", Sparse = true }),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Peca?> ObterAsync(string id, CancellationToken cancellationToken = default) =>
        await _colecao.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Peca>> ListarAsync(bool apenasAtivas = true, CancellationToken cancellationToken = default)
    {
        var filtro = apenasAtivas
            ? Builders<Peca>.Filter.Eq(p => p.Ativo, true)
            : Builders<Peca>.Filter.Empty;

        return await _colecao.Find(filtro).SortBy(p => p.Nome).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InserirAsync(Peca peca, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peca);

        try
        {
            await _colecao.InsertOneAsync(peca, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException excecao) when (excecao.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<Peca?> DarEntradaAsync(string id, int quantidade, CancellationToken cancellationToken = default)
    {
        if (quantidade <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A entrada tem que ser positiva.");

        return await _colecao.FindOneAndUpdateAsync(
            p => p.Id == id,
            Builders<Peca>.Update.Inc(p => p.QuantidadeEmEstoque, quantidade),
            new FindOneAndUpdateOptions<Peca> { ReturnDocument = ReturnDocument.After },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReservarAsync(
        string id, int idOrdem, int quantidade, DateTime agora, CancellationToken cancellationToken = default)
    {
        if (quantidade <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A quantidade reservada tem que ser positiva.");

        // Ativa, sem reserva desta ordem, e com saldo: estoque menos a soma das
        // reservas, calculado pelo próprio Mongo no momento da escrita.
        var filtro = new BsonDocument
        {
            { "_id", id },
            { "Ativo", true },
            { "Reservas.IdOrdem", new BsonDocument("$ne", idOrdem) },
            {
                "$expr", new BsonDocument("$gte", new BsonArray
                {
                    new BsonDocument("$subtract", new BsonArray
                    {
                        "$QuantidadeEmEstoque",
                        new BsonDocument("$sum", "$Reservas.Quantidade"),
                    }),
                    quantidade,
                })
            },
        };

        var reserva = Builders<Peca>.Update.Push(
            p => p.Reservas,
            new ReservaDePeca { IdOrdem = idOrdem, Quantidade = quantidade, ReservadaEm = agora });

        for (var tentativa = 1; ; tentativa++)
        {
            var resultado = await _colecao.UpdateOneAsync(filtro, reserva, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (resultado.ModifiedCount == 1)
                return true;

            // Não casou. A regra pura, sobre o documento como está agora, diz
            // o porquê com a mensagem certa — ou que a reserva já existia.
            var peca = await ObterAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new PecaNaoEncontradaException(id);

            if (!Estoque.Reservar(peca, idOrdem, quantidade, agora))
                return false;

            // A regra pura aceitaria: o saldo mudou entre a escrita e a
            // leitura. Tenta de novo, com limite.
            if (tentativa >= TentativasDeReserva)
                throw new EstoqueInsuficienteException(peca, quantidade);
        }
    }

    public async Task<bool> DevolverAsync(string id, int idOrdem, CancellationToken cancellationToken = default)
    {
        var resultado = await _colecao.UpdateOneAsync(
            p => p.Id == id,
            Builders<Peca>.Update.PullFilter(p => p.Reservas, r => r.IdOrdem == idOrdem),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return resultado.ModifiedCount == 1;
    }

    public async Task<long> DevolverTodasDaOrdemAsync(int idOrdem, CancellationToken cancellationToken = default)
    {
        var resultado = await _colecao.UpdateManyAsync(
            Builders<Peca>.Filter.ElemMatch(p => p.Reservas, r => r.IdOrdem == idOrdem),
            Builders<Peca>.Update.PullFilter(p => p.Reservas, r => r.IdOrdem == idOrdem),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return resultado.ModifiedCount;
    }

    public async Task<bool> BaixarAsync(string id, int idOrdem, CancellationToken cancellationToken = default)
    {
        // Pipeline de atualização: num só $set, o estoque perde a quantidade
        // reservada pela ordem e a reserva sai da lista. As duas expressões
        // leem o documento de antes do $set, então enxergam a mesma reserva.
        var daOrdem = new BsonDocument("$filter", new BsonDocument
        {
            { "input", "$Reservas" },
            { "cond", new BsonDocument("$eq", new BsonArray { "$$this.IdOrdem", idOrdem }) },
        });

        var dasOutras = new BsonDocument("$filter", new BsonDocument
        {
            { "input", "$Reservas" },
            { "cond", new BsonDocument("$ne", new BsonArray { "$$this.IdOrdem", idOrdem }) },
        });

        var estagio = new BsonDocument("$set", new BsonDocument
        {
            {
                "QuantidadeEmEstoque", new BsonDocument("$subtract", new BsonArray
                {
                    "$QuantidadeEmEstoque",
                    new BsonDocument("$sum", new BsonDocument("$map", new BsonDocument
                    {
                        { "input", daOrdem },
                        { "in", "$$this.Quantidade" },
                    })),
                })
            },
            { "Reservas", dasOutras },
        });

        // Só casa se a ordem tem reserva: sem reserva não há baixa, e repetir
        // não tira a peça duas vezes.
        var filtro = new BsonDocument
        {
            { "_id", id },
            { "Reservas.IdOrdem", idOrdem },
        };

        var resultado = await _colecao.UpdateOneAsync(
            filtro,
            new PipelineUpdateDefinition<Peca>(new[] { estagio }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return resultado.ModifiedCount == 1;
    }
}
