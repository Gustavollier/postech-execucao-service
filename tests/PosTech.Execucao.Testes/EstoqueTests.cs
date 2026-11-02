using PosTech.Execucao;

namespace PosTech.Execucao.Testes;

public class EstoqueTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Peca UmaPeca(int emEstoque = 10) => new()
    {
        Nome = "Filtro de óleo", Marca = "Bosch", Codigo = "FO-123",
        Preco = 38.90m, QuantidadeEmEstoque = emEstoque,
    };

    [Fact]
    public void Reservar_separa_a_peca_sem_tirar_do_estoque()
    {
        var peca = UmaPeca();

        Assert.True(Estoque.Reservar(peca, idOrdem: 1, quantidade: 3, Agora));

        Assert.Equal(10, peca.QuantidadeEmEstoque);
        Assert.Equal(3, peca.QuantidadeReservada);
        Assert.Equal(7, peca.Disponivel);
    }

    [Fact]
    public void Dois_orcamentos_nao_prometem_a_mesma_peca()
    {
        // É exatamente para isto que a reserva acontece no diagnóstico, e não
        // na execução.
        var peca = UmaPeca(emEstoque: 5);

        Estoque.Reservar(peca, 1, 3, Agora);

        var falta = Assert.Throws<EstoqueInsuficienteException>(() => Estoque.Reservar(peca, 2, 3, Agora));
        Assert.Equal(2, falta.Disponivel);
        Assert.Equal(3, falta.Pedido);
        Assert.Equal(3, peca.QuantidadeReservada);
    }

    [Fact]
    public void Reservar_de_novo_para_a_mesma_ordem_nao_reserva_duas_vezes()
    {
        var peca = UmaPeca();

        Assert.True(Estoque.Reservar(peca, 1, 3, Agora));
        Assert.False(Estoque.Reservar(peca, 1, 3, Agora));

        Assert.Equal(3, peca.QuantidadeReservada);
    }

    [Fact]
    public void Peca_inativa_nao_entra_em_orcamento_novo()
    {
        var peca = UmaPeca();
        peca.Ativo = false;

        Assert.Throws<InvalidOperationException>(() => Estoque.Reservar(peca, 1, 1, Agora));
    }

    [Fact]
    public void Devolver_repetido_nao_mexe_na_reserva_de_outra_ordem()
    {
        // O defeito que a versão com contador tinha. Duas ordens reservam a
        // mesma peça; o OrdemCancelada da primeira chega duas vezes, que a
        // fila faz por desenho. Com um contador e piso em zero, a segunda
        // devolução consumia a reserva da ordem 2 — e a peça prometida a um
        // cliente voltava a ficar disponível para outro.
        var peca = UmaPeca(emEstoque: 4);
        Estoque.Reservar(peca, idOrdem: 1, quantidade: 2, Agora);
        Estoque.Reservar(peca, idOrdem: 2, quantidade: 2, Agora);

        Assert.True(Estoque.Devolver(peca, 1));
        Assert.False(Estoque.Devolver(peca, 1));

        Assert.Equal(2, peca.QuantidadeReservada);
        Assert.Equal(2, Assert.Single(peca.Reservas).IdOrdem);
        Assert.Equal(2, peca.Disponivel);
    }

    [Fact]
    public void Baixar_tira_do_estoque_e_libera_a_reserva()
    {
        var peca = UmaPeca(emEstoque: 10);
        Estoque.Reservar(peca, 1, 3, Agora);

        Assert.True(Estoque.Baixar(peca, 1));

        Assert.Equal(7, peca.QuantidadeEmEstoque);
        Assert.Equal(0, peca.QuantidadeReservada);
        Assert.Equal(7, peca.Disponivel);
    }

    [Fact]
    public void Baixar_repetido_nao_tira_a_peca_duas_vezes()
    {
        var peca = UmaPeca(emEstoque: 10);
        Estoque.Reservar(peca, 1, 3, Agora);

        Estoque.Baixar(peca, 1);

        Assert.False(Estoque.Baixar(peca, 1));
        Assert.Equal(7, peca.QuantidadeEmEstoque);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reservar_quantidade_nao_positiva_nao_passa(int quantidade)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Estoque.Reservar(UmaPeca(), 1, quantidade, Agora));
    }

    [Fact]
    public void Entrada_soma_ao_estoque()
    {
        var peca = UmaPeca(emEstoque: 2);

        Estoque.DarEntrada(peca, 5);

        Assert.Equal(7, peca.QuantidadeEmEstoque);
        Assert.Throws<ArgumentOutOfRangeException>(() => Estoque.DarEntrada(peca, 0));
    }
}
