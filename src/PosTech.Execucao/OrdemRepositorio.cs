using MongoDB.Driver;
using PosTech.Mensageria;

namespace PosTech.Execucao;

public interface IOrdemRepositorio
{
    Task<OrdemEmExecucao?> ObterPorOrdemAsync(int idOrdem, CancellationToken cancellationToken = default);

    /// <summary>
    /// A fila da oficina: o que ainda não terminou, na ordem em que o
    /// mecânico deve pegar — primeiro o que já está na bancada, depois o que
    /// espera diagnóstico, depois o que espera pagamento; dentro de cada
    /// etapa, a mais antiga primeiro.
    /// </summary>
    Task<IReadOnlyList<OrdemEmExecucao>> ListarFilaAsync(
        EEtapaDeExecucao? etapa = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava uma ordem nova. Devolve <c>false</c>, sem erro, quando a ordem já
    /// existe: é o que acontece quando o mesmo <c>OrdemAberta</c> chega duas
    /// vezes e as duas entregas passam juntas pela checagem de idempotência.
    /// </summary>
    Task<bool> InserirAsync(OrdemEmExecucao ordem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava o documento inteiro, eventos pendentes incluídos — uma escrita
    /// só, atômica por ser de um documento, que é como o outbox funciona aqui.
    ///
    /// Exige que a versão no banco seja a lida; se alguém gravou no meio,
    /// lança <see cref="ConflitoDeConcorrenciaException"/> e nada é escrito.
    /// </summary>
    Task SalvarAsync(OrdemEmExecucao ordem, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outra gravação chegou primeiro. Quem chamou relê e decide de novo: na API
/// isso vira 409; no consumidor, a exceção faz a mensagem ser reentregue e
/// reprocessada sobre o estado novo.
/// </summary>
public sealed class ConflitoDeConcorrenciaException(int idOrdem)
    : Exception($"A ordem {idOrdem} foi alterada por outra operação. Recarregue e tente de novo.")
{
    public int IdOrdem { get; } = idOrdem;
}

public sealed class OrdemRepositorio : IOrdemRepositorio
{
    public const string Colecao = "ordens";

    private readonly IMongoCollection<OrdemEmExecucao> _colecao;

    public OrdemRepositorio(IMongoDatabase banco)
    {
        ArgumentNullException.ThrowIfNull(banco);

        _colecao = banco.GetCollection<OrdemEmExecucao>(Colecao);
    }

    /// <summary>
    /// Índices. Idempotente: rodar na subida do serviço é seguro.
    ///
    /// O índice em IdOrdem é único porque uma ordem entra na fila uma vez só,
    /// e deixar isso só na disciplina da aplicação é como a Fase 3 ficou sem
    /// UNIQUE em CPF.
    /// </summary>
    public async Task GarantirIndicesAsync(CancellationToken cancellationToken = default)
    {
        await _colecao.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<OrdemEmExecucao>(
                    Builders<OrdemEmExecucao>.IndexKeys.Ascending(o => o.IdOrdem),
                    new CreateIndexOptions { Unique = true, Name = "ux_idOrdem" }),

                // A tela da fila pergunta sempre por etapa, da mais antiga
                // para a mais nova.
                new CreateIndexModel<OrdemEmExecucao>(
                    Builders<OrdemEmExecucao>.IndexKeys.Ascending(o => o.Etapa).Ascending(o => o.AbertaEm),
                    new CreateIndexOptions { Name = "ix_etapa_abertaEm" }),

                // O publicador pergunta sempre quem tem evento pendente.
                new CreateIndexModel<OrdemEmExecucao>(
                    Builders<OrdemEmExecucao>.IndexKeys.Ascending("EventosPendentes.ProximaTentativaEm"),
                    new CreateIndexOptions { Name = "ix_eventosPendentes", Sparse = true }),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OrdemEmExecucao?> ObterPorOrdemAsync(int idOrdem, CancellationToken cancellationToken = default) =>
        await _colecao.Find(o => o.IdOrdem == idOrdem).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<OrdemEmExecucao>> ListarFilaAsync(
        EEtapaDeExecucao? etapa = null, CancellationToken cancellationToken = default)
    {
        var filtro = etapa is { } unica
            ? Builders<OrdemEmExecucao>.Filter.Eq(o => o.Etapa, unica)
            : Builders<OrdemEmExecucao>.Filter.In(o => o.Etapa, PrioridadeDaFila);

        var ordens = await _colecao.Find(filtro).ToListAsync(cancellationToken).ConfigureAwait(false);

        // A prioridade não segue o valor numérico do enum, então a ordenação
        // fica aqui. A fila de uma oficina cabe em memória com folga.
        return ordens
            .OrderBy(o => Prioridade(o.Etapa))
            .ThenBy(o => o.AbertaEm)
            .ToList();
    }

    private static readonly EEtapaDeExecucao[] PrioridadeDaFila =
    [
        EEtapaDeExecucao.EmExecucao,
        EEtapaDeExecucao.AguardandoDiagnostico,
        EEtapaDeExecucao.AguardandoLiberacao,
    ];

    private static int Prioridade(EEtapaDeExecucao etapa)
    {
        var posicao = Array.IndexOf(PrioridadeDaFila, etapa);
        return posicao < 0 ? int.MaxValue : posicao;
    }

    public async Task<bool> InserirAsync(OrdemEmExecucao ordem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        ordem.Versao = 1;

        try
        {
            await _colecao.InsertOneAsync(ordem, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException excecao) when (excecao.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            ordem.Versao = 0;
            return false;
        }
    }

    public async Task SalvarAsync(OrdemEmExecucao ordem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        var versaoLida = ordem.Versao;
        ordem.Versao = versaoLida + 1;

        var resultado = await _colecao.ReplaceOneAsync(
            o => o.Id == ordem.Id && o.Versao == versaoLida,
            ordem,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (resultado.MatchedCount == 0)
        {
            ordem.Versao = versaoLida;
            throw new ConflitoDeConcorrenciaException(ordem.IdOrdem);
        }
    }
}

/// <summary>
/// Outbox do Execução: lê e remove os eventos embutidos nas ordens.
///
/// Implementa a mesma interface do outbox SQL do OS Service, então o
/// <see cref="ProcessadorDoOutbox"/> funciona igual sem saber onde o dado mora.
///
/// Não mexe em <see cref="OrdemEmExecucao.Versao"/> de propósito. Se mexesse,
/// toda publicação faria a próxima gravação do mecânico falhar por conflito.
/// O custo de não mexer: uma gravação da aplicação que leu o documento antes
/// da publicação regrava o evento já publicado, e ele sai de novo. Entrega
/// repetida é o regime normal do broker, e todo consumidor já descarta pela
/// <c>MensagemId</c>.
/// </summary>
public sealed class OutboxMongo : IRepositorioDeOutbox
{
    private readonly IMongoCollection<OrdemEmExecucao> _colecao;

    public OutboxMongo(IMongoDatabase banco)
    {
        ArgumentNullException.ThrowIfNull(banco);

        _colecao = banco.GetCollection<OrdemEmExecucao>(OrdemRepositorio.Colecao);
    }

    public async Task<IReadOnlyList<EventoOutbox>> ObterPendentesAsync(int limite, CancellationToken cancellationToken = default)
    {
        var agora = DateTime.UtcNow;

        var filtro = Builders<OrdemEmExecucao>.Filter.ElemMatch(
            o => o.EventosPendentes,
            e => e.Status == EStatusOutbox.Pendente && e.ProximaTentativaEm <= agora);

        var documentos = await _colecao.Find(filtro).Limit(limite).ToListAsync(cancellationToken).ConfigureAwait(false);

        return documentos
            .SelectMany(d => d.EventosPendentes)
            .Where(e => e.Status == EStatusOutbox.Pendente && e.ProximaTentativaEm <= agora)
            .OrderBy(e => e.CriadoEm)
            .Take(limite)
            .ToList();
    }

    /// <summary>
    /// Publicado sai do documento: manter histórico de publicação dentro do
    /// agregado faria o documento crescer sem teto.
    /// </summary>
    public async Task MarcarPublicadoAsync(Guid mensagemId, DateTime publicadoEm, CancellationToken cancellationToken = default)
    {
        var filtro = Builders<OrdemEmExecucao>.Filter.ElemMatch(o => o.EventosPendentes, e => e.MensagemId == mensagemId);
        var remocao = Builders<OrdemEmExecucao>.Update.PullFilter(o => o.EventosPendentes, e => e.MensagemId == mensagemId);

        await _colecao.UpdateOneAsync(filtro, remocao, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RegistrarFalhaAsync(
        Guid mensagemId, int tentativas, DateTime proximaTentativaEm, string erro, EStatusOutbox status,
        CancellationToken cancellationToken = default)
    {
        var filtro = Builders<OrdemEmExecucao>.Filter.ElemMatch(o => o.EventosPendentes, e => e.MensagemId == mensagemId);

        var atualizacao = Builders<OrdemEmExecucao>.Update
            .Set("EventosPendentes.$.Tentativas", tentativas)
            .Set("EventosPendentes.$.ProximaTentativaEm", proximaTentativaEm)
            .Set("EventosPendentes.$.UltimoErro", erro)
            .Set("EventosPendentes.$.Status", status);

        await _colecao.UpdateOneAsync(filtro, atualizacao, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
