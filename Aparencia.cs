using System.Windows;
using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv;

/// <summary>
/// A camada WPF do tema — fininha de propósito: a decisão mora em
/// <see cref="Tema"/> (Núcleo, pura, testada); aqui só se troca o dicionário.
///
/// App.xaml carrega dois MergedDictionaries: [0] = paleta (Temas/Escuro.xaml),
/// [1] = Estilos.xaml. Trocar o tema é substituir o [0] — medido em ~2,5 ms,
/// nenhuma janela é recriada, a venda em andamento nem percebe.
/// </summary>
public static class Aparencia
{
    public static ModoTema Atual { get; private set; } = ModoTema.Escuro;

    /// <summary>A Venda assina para repintar o que ela desenha em C# (cards de
    /// categoria/produto), que não segue DynamicResource.</summary>
    public static event Action? Mudou;

    public static void Aplicar(ModoTema modo)
    {
        if (modo == Atual) return;

        var dic = new ResourceDictionary
        {
            Source = new Uri(modo == ModoTema.Claro ? "Temas/Claro.xaml" : "Temas/Escuro.xaml",
                             UriKind.Relative),
        };
        Application.Current.Resources.MergedDictionaries[0] = dic;
        Atual = modo;
        Mudou?.Invoke();
    }

    /// <summary>Lê a config do terminal e decide o tema para ESTE instante.</summary>
    public static ModoTema Resolver(SqliteConnection cx)
    {
        var (de, ate) = Tema.LerJanela(
            Vendas.Config(cx, "tema_claro_de"),
            Vendas.Config(cx, "tema_claro_ate"));
        return Tema.Decidir(Vendas.Config(cx, "tema"), de, ate, DateTime.Now);
    }
}
