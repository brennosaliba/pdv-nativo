using System.Windows;
using Pdv.Nucleo;

namespace Pdv.Telas;

/// <summary>
/// O lado de WPF da autorização de estorno: é isto que a máquina de estados do
/// núcleo (<see cref="Autorizacao.ResolverAsync"/>) chama quando precisa de
/// janela. Fica separado para a decisão continuar testável sem abrir tela.
///
/// Só duas coisas precisam de janela: o aviso de espera enquanto a nuvem
/// confere, e a tela do código do autenticador do dono. Não há PIN, não há
/// senha, não há escolha depois da falha: o operador digita o código que o dono
/// passar, ou cancela.
/// </summary>
public sealed class TelaAutorizacao : ITelaAutorizacao
{
    private readonly Window _dono;

    public TelaAutorizacao(Window dono) => _dono = dono;

    public IDisposable Aguardando(string mensagem) => new Espera(_dono, mensagem);

    public Task<string?> PedirCodigoAsync(string? aviso)
        => Task.FromResult(PedirCodigo.Mostrar(_dono, aviso));
}
