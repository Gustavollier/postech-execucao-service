using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Execucao;

/// <summary>
/// As transições da ordem dentro da oficina.
///
/// Função pura, sem banco e sem broker, como o orquestrador e o montador de
/// orçamento. Recebe o documento e devolve ele mudado, com os eventos já
/// enfileirados no próprio agregado.
/// </summary>
public static class GestorDaExecucao
{
    public const string Origem = "execucao-service";

    /// <summary>
    /// Chegou uma ordem. Entra na fila esperando diagnóstico, sem publicar nada:
    /// receber não é fato novo para ninguém de fora.
    /// </summary>
    public static OrdemEmExecucao Receber(OrdemAberta evento, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(evento);

        return new OrdemEmExecucao
        {
            IdOrdem = evento.IdOrdem,
            IdCliente = evento.IdCliente,
            NomeCliente = evento.NomeCliente,
            EmailCliente = evento.EmailCliente,
            IdVeiculo = evento.IdVeiculo,
            DescricaoVeiculo = evento.DescricaoVeiculo,
            IdFuncionarioResponsavel = evento.IdFuncionario,
            Etapa = EEtapaDeExecucao.AguardandoDiagnostico,
            AbertaEm = evento.AbertaEm,
        };
    }

    /// <summary>
    /// O mecânico terminou o diagnóstico e lançou os itens.
    ///
    /// Este é o passo que o mapeamento do schema revelou: é daqui que sai o
    /// valor do orçamento, e é por isso que o evento leva os itens dentro, em
    /// vez de o Billing ir buscá-los (ver fase4/mapa-do-schema.md).
    /// </summary>
    public static void ConcluirDiagnostico(
        OrdemEmExecucao ordem,
        IReadOnlyList<ItemLancado> itens,
        string correlationId,
        DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(ordem);
        ArgumentNullException.ThrowIfNull(itens);

        GarantirQuePodeDiagnosticar(ordem);
        ValidarItens(itens);

        ordem.Itens = [.. itens];
        ordem.ValorTotal = itens.Sum(i => i.Subtotal);
        ordem.Etapa = EEtapaDeExecucao.AguardandoLiberacao;
        ordem.DiagnosticadaEm = agora;

        ordem.EventosPendentes.Add(Outbox.Enfileirar(
            new DiagnosticoConcluido(
                ordem.IdOrdem,
                ordem.IdFuncionarioResponsavel,
                // O cliente viaja junto: é o que permite ao Billing montar o
                // orçamento e criar a cobrança sem consultar o Identidade.
                ordem.IdCliente,
                ordem.NomeCliente,
                ordem.EmailCliente,
                [.. itens.Select(i => new ItemDaOrdem(i.Descricao, i.Tipo, i.Quantidade, i.ValorUnitario))],
                ordem.ValorTotal,
                agora),
            correlationId,
            Origem,
            agora));
    }

    /// <summary>
    /// A ordem está esperando diagnóstico? Separado de
    /// <see cref="ConcluirDiagnostico"/> para ser checado <b>antes</b> de
    /// reservar peça: reservar e só depois descobrir que a ordem já tinha sido
    /// diagnosticada deixaria uma reserva para desfazer à toa.
    /// </summary>
    public static void GarantirQuePodeDiagnosticar(OrdemEmExecucao ordem)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        if (ordem.Etapa != EEtapaDeExecucao.AguardandoDiagnostico)
            throw new InvalidOperationException(
                $"Só uma ordem aguardando diagnóstico pode ser diagnosticada. Esta está {ordem.Etapa}.");
    }

    /// <summary>
    /// As regras de forma dos itens. Pública pelo mesmo motivo da checagem de
    /// etapa: roda antes da reserva.
    /// </summary>
    public static void ValidarItens(IReadOnlyList<ItemLancado> itens)
    {
        ArgumentNullException.ThrowIfNull(itens);

        if (itens.Count == 0)
            throw new ArgumentException("Um diagnóstico sem itens não fecha.", nameof(itens));

        foreach (var item in itens)
        {
            if (string.IsNullOrWhiteSpace(item.Descricao))
                throw new ArgumentException("Todo item precisa de descrição.", nameof(itens));

            if (item.Quantidade <= 0)
                throw new ArgumentException($"Quantidade inválida em '{item.Descricao}'.", nameof(itens));

            if (item.ValorUnitario < 0)
                throw new ArgumentException($"Valor negativo em '{item.Descricao}'.", nameof(itens));

            if (item.Tipo == ETipoItem.Peca && string.IsNullOrWhiteSpace(item.IdPeca))
                throw new ArgumentException($"O item de peça '{item.Descricao}' precisa apontar para uma peça.", nameof(itens));

            if (item.Tipo == ETipoItem.MaoDeObra && !string.IsNullOrWhiteSpace(item.IdPeca))
                throw new ArgumentException($"Mão de obra não aponta para peça: '{item.Descricao}'.", nameof(itens));
        }
    }

    /// <summary>
    /// O pagamento entrou e a oficina pode trabalhar.
    ///
    /// Idempotente de propósito: <c>PagamentoConfirmado</c> chega ao menos uma
    /// vez, e a segunda entrega não pode virar erro.
    /// </summary>
    public static void Liberar(OrdemEmExecucao ordem, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        if (ordem.Etapa is EEtapaDeExecucao.EmExecucao or EEtapaDeExecucao.Finalizada)
            return;

        if (ordem.Etapa != EEtapaDeExecucao.AguardandoLiberacao)
            throw new InvalidOperationException(
                $"Só uma ordem diagnosticada pode ser liberada. Esta está {ordem.Etapa}.");

        ordem.Etapa = EEtapaDeExecucao.EmExecucao;
    }

    public static void Finalizar(OrdemEmExecucao ordem, string correlationId, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        if (ordem.Etapa == EEtapaDeExecucao.Finalizada)
            return;

        if (ordem.Etapa != EEtapaDeExecucao.EmExecucao)
            throw new InvalidOperationException(
                $"Só uma ordem em execução pode ser finalizada. Esta está {ordem.Etapa}.");

        ordem.Etapa = EEtapaDeExecucao.Finalizada;
        ordem.FinalizadaEm = agora;

        ordem.EventosPendentes.Add(Outbox.Enfileirar(
            new ExecucaoFinalizada(ordem.IdOrdem, ordem.IdFuncionarioResponsavel, agora),
            correlationId,
            Origem,
            agora));
    }

    /// <summary>
    /// Compensação vinda do orquestrador: a saga desistiu.
    ///
    /// Não publica nada — quem cancelou já anunciou. Devolve <c>false</c> e não
    /// faz nada quando a ordem já está finalizada: um cancelamento que chega
    /// depois do trabalho pronto não pode apagar o trabalho, e é o tipo de
    /// corrida que uma fila produz de verdade.
    /// </summary>
    public static bool Cancelar(OrdemEmExecucao ordem, string motivo, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        if (ordem.Etapa == EEtapaDeExecucao.Finalizada)
            return false;

        if (ordem.Etapa == EEtapaDeExecucao.Cancelada)
            return true;

        ordem.Etapa = EEtapaDeExecucao.Cancelada;
        ordem.MotivoDoCancelamento = motivo;

        return true;
    }

    /// <summary>
    /// As peças que uma ordem precisa reservar ou devolver, agrupadas por peça.
    /// Separado das transições porque o estoque é outro agregado, com o seu
    /// próprio documento e a sua própria escrita.
    /// </summary>
    public static IReadOnlyDictionary<string, int> PecasDa(OrdemEmExecucao ordem)
    {
        ArgumentNullException.ThrowIfNull(ordem);

        return ordem.Itens
            .Where(i => i.Tipo == ETipoItem.Peca && !string.IsNullOrWhiteSpace(i.IdPeca))
            .GroupBy(i => i.IdPeca!)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantidade));
    }
}

/// <summary>
/// Reserva e devolução de peças, por ordem.
///
/// Reservar no diagnóstico, e não na execução, é o que impede dois orçamentos
/// prometerem a mesma peça. O preço disso é que a reserva vira mais um passo
/// que a compensação precisa desfazer.
///
/// Toda operação é chaveada pela ordem e idempotente: a fila entrega ao menos
/// uma vez, e o HTTP do mecânico pode repetir. Estas são as regras puras; o
/// <c>PecaRepositorio</c> aplica as mesmas regras como escrita atômica no
/// Mongo, que é o que segura duas reservas simultâneas.
/// </summary>
public static class Estoque
{
    /// <summary>
    /// Separa a peça para a ordem. Reservar de novo para a mesma ordem não
    /// muda nada e devolve <c>false</c>.
    /// </summary>
    public static bool Reservar(Peca peca, int idOrdem, int quantidade, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(peca);

        if (quantidade <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A quantidade reservada tem que ser positiva.");

        if (peca.Reservas.Any(r => r.IdOrdem == idOrdem))
            return false;

        if (!peca.Ativo)
            throw new InvalidOperationException($"A peça '{peca.Nome}' está inativa e não pode ser reservada.");

        if (peca.Disponivel < quantidade)
            throw new EstoqueInsuficienteException(peca, quantidade);

        peca.Reservas.Add(new ReservaDePeca { IdOrdem = idOrdem, Quantidade = quantidade, ReservadaEm = agora });

        return true;
    }

    /// <summary>
    /// Devolve o que a ordem tinha reservado. Só mexe na reserva daquela ordem:
    /// uma devolução repetida não encontra nada e não pode tocar na reserva de
    /// outra.
    /// </summary>
    public static bool Devolver(Peca peca, int idOrdem)
    {
        ArgumentNullException.ThrowIfNull(peca);

        return peca.Reservas.RemoveAll(r => r.IdOrdem == idOrdem) > 0;
    }

    /// <summary>
    /// A peça saiu do estoque de verdade, quando o serviço foi executado. A
    /// reserva da ordem vira baixa — e sem reserva não há baixa, então repetir
    /// não tira a peça duas vezes.
    /// </summary>
    public static bool Baixar(Peca peca, int idOrdem)
    {
        ArgumentNullException.ThrowIfNull(peca);

        var reserva = peca.Reservas.FirstOrDefault(r => r.IdOrdem == idOrdem);

        if (reserva is null)
            return false;

        peca.QuantidadeEmEstoque -= reserva.Quantidade;
        peca.Reservas.Remove(reserva);

        return true;
    }

    public static void DarEntrada(Peca peca, int quantidade)
    {
        ArgumentNullException.ThrowIfNull(peca);

        if (quantidade <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A entrada tem que ser positiva.");

        peca.QuantidadeEmEstoque += quantidade;
    }
}

/// <summary>
/// Não há peça suficiente para a reserva. Tipo próprio porque é a falha de
/// negócio que a API devolve como 409 com a peça e os números, e não como erro
/// genérico.
/// </summary>
public sealed class EstoqueInsuficienteException(Peca peca, int pedido)
    : InvalidOperationException(
        $"Estoque insuficiente de '{peca.Nome}': disponível {peca.Disponivel}, pedido {pedido}.")
{
    public string IdPeca { get; } = peca.Id;

    public int Disponivel { get; } = peca.Disponivel;

    public int Pedido { get; } = pedido;
}
