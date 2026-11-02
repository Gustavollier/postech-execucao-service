using PosTech.Contratos;

namespace PosTech.Execucao;

/// <summary>Um item como o mecânico lança, antes de o preço ser fechado.</summary>
public sealed record ItemPedido(
    string? Descricao,
    ETipoItem Tipo,
    string? IdPeca,
    int Quantidade,
    decimal ValorUnitario);

public sealed class OrdemNaoEncontradaException(int idOrdem)
    : Exception($"A ordem {idOrdem} não está na fila da oficina.")
{
    public int IdOrdem { get; } = idOrdem;
}

/// <summary>O que aconteceu com um evento que chegou pela fila.</summary>
public enum EResultadoDoEvento
{
    Aplicado,

    /// <summary>Já estava aplicado: entrega repetida, nada mudou.</summary>
    JaAplicado,

    /// <summary>O estado da ordem não admite o evento, e ele foi descartado com log.</summary>
    Ignorado,

    /// <summary>A ordem não existe neste serviço.</summary>
    OrdemDesconhecida,
}

/// <summary>
/// Os casos de uso da oficina: costura as regras puras de
/// <see cref="GestorDaExecucao"/> e <see cref="Estoque"/> com os repositórios.
///
/// A ordem das escritas é a decisão que importa aqui, porque a ordem e as
/// peças são documentos diferentes e não há transação entre eles (ADR-0007).
/// A regra geral: primeiro o que é idempotente, por último a gravação da
/// ordem. Se o processo morrer no meio, repetir a operação inteira é seguro —
/// as reservas, devoluções e baixas já feitas não se repetem, porque são
/// chaveadas pela ordem.
/// </summary>
public sealed class ServicoDaOficina
{
    private readonly IOrdemRepositorio _ordens;
    private readonly IPecaRepositorio _pecas;
    private readonly Func<DateTime> _agora;

    public ServicoDaOficina(IOrdemRepositorio ordens, IPecaRepositorio pecas, Func<DateTime>? agora = null)
    {
        _ordens = ordens;
        _pecas = pecas;
        _agora = agora ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// <c>OrdemAberta</c>: a ordem entra na fila. Devolve <c>false</c> quando
    /// ela já estava lá.
    /// </summary>
    public async Task<bool> ReceberAsync(OrdemAberta evento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);

        if (await _ordens.ObterPorOrdemAsync(evento.IdOrdem, cancellationToken).ConfigureAwait(false) is not null)
            return false;

        return await _ordens.InserirAsync(GestorDaExecucao.Receber(evento, _agora()), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// O mecânico fecha o diagnóstico: os preços das peças saem do catálogo,
    /// as peças são reservadas, e a ordem grava com o
    /// <c>DiagnosticoConcluido</c> no outbox.
    ///
    /// Se qualquer reserva falhar, o que já foi reservado nesta chamada é
    /// devolvido antes do erro subir: ou o diagnóstico inteiro passa, ou o
    /// estoque fica como estava.
    /// </summary>
    public async Task<OrdemEmExecucao> DiagnosticarAsync(
        int idOrdem,
        IReadOnlyList<ItemPedido> pedidos,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pedidos);

        var ordem = await _ordens.ObterPorOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false)
            ?? throw new OrdemNaoEncontradaException(idOrdem);

        GestorDaExecucao.GarantirQuePodeDiagnosticar(ordem);

        var itens = await FecharPrecosAsync(pedidos, cancellationToken).ConfigureAwait(false);

        GestorDaExecucao.ValidarItens(itens);

        var agora = _agora();
        var reservadasAgora = new List<string>();

        try
        {
            foreach (var (idPeca, quantidade) in PecasAgrupadas(itens))
            {
                if (await _pecas.ReservarAsync(idPeca, idOrdem, quantidade, agora, cancellationToken).ConfigureAwait(false))
                    reservadasAgora.Add(idPeca);
            }

            GestorDaExecucao.ConcluirDiagnostico(ordem, itens, correlationId, agora);

            await _ordens.SalvarAsync(ordem, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Devolve só o que esta chamada reservou. Reserva que já existia
            // para a ordem — de uma tentativa anterior, ou de um clique duplo
            // que chegou primeiro — não é desta chamada desfazer.
            foreach (var idPeca in reservadasAgora)
                await _pecas.DevolverAsync(idPeca, idOrdem, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        return ordem;
    }

    /// <summary>
    /// <c>PagamentoConfirmado</c>: a oficina pode começar. Um pagamento que
    /// chega para uma ordem que não está esperando — cancelada no meio, por
    /// exemplo — é descartado com log, e não vira erro: devolver erro mandaria
    /// a mensagem para a dead letter, e não há nada a reprocessar. O estorno,
    /// nesse caso, é do Billing, que escuta o mesmo <c>OrdemCancelada</c>.
    /// </summary>
    public async Task<EResultadoDoEvento> LiberarAsync(int idOrdem, CancellationToken cancellationToken = default)
    {
        var ordem = await _ordens.ObterPorOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false);

        if (ordem is null)
            return EResultadoDoEvento.OrdemDesconhecida;

        if (ordem.Etapa is EEtapaDeExecucao.EmExecucao or EEtapaDeExecucao.Finalizada)
            return EResultadoDoEvento.JaAplicado;

        if (ordem.Etapa != EEtapaDeExecucao.AguardandoLiberacao)
            return EResultadoDoEvento.Ignorado;

        GestorDaExecucao.Liberar(ordem, _agora());

        await _ordens.SalvarAsync(ordem, cancellationToken).ConfigureAwait(false);

        return EResultadoDoEvento.Aplicado;
    }

    /// <summary>
    /// O mecânico terminou: as reservas viram baixa e a ordem grava com o
    /// <c>ExecucaoFinalizada</c> no outbox. Finalizar de novo é no-op.
    ///
    /// A baixa vem antes da gravação. Se a gravação falhar, repetir é seguro:
    /// sem reserva não há baixa. Na ordem inversa, uma falha depois de gravar
    /// deixaria a reserva presa para sempre, com a peça contada como
    /// indisponível e nunca saindo do estoque.
    /// </summary>
    public async Task<OrdemEmExecucao> FinalizarAsync(
        int idOrdem, string correlationId, CancellationToken cancellationToken = default)
    {
        var ordem = await _ordens.ObterPorOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false)
            ?? throw new OrdemNaoEncontradaException(idOrdem);

        if (ordem.Etapa == EEtapaDeExecucao.Finalizada)
            return ordem;

        GestorDaExecucao.Finalizar(ordem, correlationId, _agora());

        foreach (var idPeca in GestorDaExecucao.PecasDa(ordem).Keys)
            await _pecas.BaixarAsync(idPeca, idOrdem, cancellationToken).ConfigureAwait(false);

        // O que sobrar reservado para esta ordem é resto de alguma tentativa
        // interrompida, e não vai mais ser usado.
        await _pecas.DevolverTodasDaOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false);

        await _ordens.SalvarAsync(ordem, cancellationToken).ConfigureAwait(false);

        return ordem;
    }

    /// <summary>
    /// <c>OrdemCancelada</c>, a compensação: a ordem sai da fila e as peças
    /// reservadas voltam. Não publica nada — quem cancelou já anunciou.
    ///
    /// Uma ordem já finalizada não é cancelada: um cancelamento que chega
    /// depois do trabalho pronto não pode apagar o trabalho.
    /// </summary>
    public async Task<EResultadoDoEvento> CancelarAsync(
        int idOrdem, string motivo, CancellationToken cancellationToken = default)
    {
        var ordem = await _ordens.ObterPorOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false);

        if (ordem is null)
        {
            // Sem ordem não deveria haver reserva, mas devolver não custa nada
            // e não deixa peça presa se houver.
            await _pecas.DevolverTodasDaOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false);
            return EResultadoDoEvento.OrdemDesconhecida;
        }

        var jaEstavaCancelada = ordem.Etapa == EEtapaDeExecucao.Cancelada;

        if (!GestorDaExecucao.Cancelar(ordem, motivo, _agora()))
            return EResultadoDoEvento.Ignorado;

        await _pecas.DevolverTodasDaOrdemAsync(idOrdem, cancellationToken).ConfigureAwait(false);

        if (jaEstavaCancelada)
            return EResultadoDoEvento.JaAplicado;

        await _ordens.SalvarAsync(ordem, cancellationToken).ConfigureAwait(false);

        return EResultadoDoEvento.Aplicado;
    }

    /// <summary>
    /// Transforma o pedido do mecânico em itens com preço fechado. Peça leva o
    /// preço do catálogo do dia, e não o que veio na requisição: o orçamento
    /// sai daqui, e quem lança o item não escolhe o preço da peça.
    /// </summary>
    private async Task<List<ItemLancado>> FecharPrecosAsync(
        IReadOnlyList<ItemPedido> pedidos, CancellationToken cancellationToken)
    {
        var itens = new List<ItemLancado>(pedidos.Count);

        foreach (var pedido in pedidos)
        {
            var item = new ItemLancado
            {
                Descricao = pedido.Descricao?.Trim() ?? string.Empty,
                Tipo = pedido.Tipo,
                IdPeca = string.IsNullOrWhiteSpace(pedido.IdPeca) ? null : pedido.IdPeca.Trim(),
                Quantidade = pedido.Quantidade,
                ValorUnitario = pedido.ValorUnitario,
            };

            if (item is { Tipo: ETipoItem.Peca, IdPeca: { } idPeca })
            {
                var peca = await _pecas.ObterAsync(idPeca, cancellationToken).ConfigureAwait(false)
                    ?? throw new PecaNaoEncontradaException(idPeca);

                item.ValorUnitario = peca.Preco;

                if (string.IsNullOrWhiteSpace(item.Descricao))
                    item.Descricao = peca.Nome;
            }

            itens.Add(item);
        }

        return itens;
    }

    private static IEnumerable<(string IdPeca, int Quantidade)> PecasAgrupadas(IEnumerable<ItemLancado> itens) =>
        itens
            .Where(i => i is { Tipo: ETipoItem.Peca, IdPeca: not null })
            .GroupBy(i => i.IdPeca!)
            .Select(g => (g.Key, g.Sum(i => i.Quantidade)));
}
