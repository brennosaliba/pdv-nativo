using System.IO;
using Microsoft.Win32;
using Pdv.Nucleo;

namespace Pdv.Instalador;

/// <summary>
/// DESINSTALAR DESFAZ O QUIOSQUE DO USUÁRIO DO CAIXA (1.0.36, 14/10/2026).
///
/// Sem isto, desinstalar deixava no registro do usuário do caixa o Shell do Windows apontando para
/// um Pdv.exe apagado e as quatro políticas da trava do Ctrl+Alt+Del: na próxima entrada, tela preta
/// e um Ctrl+Alt+Del só com Cancelar. A regra (quem é nosso, o que tirar, em que ordem) é pura e mora
/// em <see cref="DesfazerQuiosque"/>; aqui fica só o registro.
///
/// O instalador não sabe quem é o usuário do caixa (ele grava a máquina, e o Adicionar/Remover
/// Programas pode abri-lo com a conta de outro administrador). Então percorre os perfis CARREGADOS
/// em HKEY_USERS: quem desinstala pela área de trabalho do caixa tem o perfil dele carregado. Perfil
/// não carregado (o caixa não está logado) fica como está: carregar o NTUSER.DAT de outra pessoa
/// não é trabalho de desinstalador.
///
/// A ORDEM (revisão do desinstalador): o Shell sai PRIMEIRO (é ele que evita a tela preta), depois
/// cada política com o seu próprio try, e a marca da trava por último, só se todas saíram. O que não
/// saiu volta no <see cref="Desfeito"/>: Shell nosso que ficou segura a desinstalação (Instalacao).
///
/// Só mexe em HKEY_USERS\SID (nunca no HKLM nem no HKCU de quem roda o desinstalador), e a raiz vem
/// de fora: HKEY_USERS na loja, uma chave isolada na bateria.
/// </summary>
public static class QuiosqueDoCaixa
{
    /// <summary>
    /// O que aconteceu num perfil em que havia algo nosso.
    /// </summary>
    /// <param name="TirouShell">O Shell do caixa saiu.</param>
    /// <param name="Politicas">Quantas políticas da trava saíram.</param>
    /// <param name="TirouMarca">A marca da trava saiu (só sai depois de todas as políticas).</param>
    /// <param name="ShellFicou">O Shell era nosso e continua lá.</param>
    /// <param name="TravaFicou">Uma política ou a marca da trava continua lá.</param>
    public sealed record Desfeito(string Sid, bool TirouShell, int Politicas, bool TirouMarca, bool ShellFicou, bool TravaFicou)
    {
        public bool AlgumaCoisaSaiu => TirouShell || Politicas > 0 || TirouMarca;
    }

    /// <summary>
    /// Percorre os perfis embaixo de <paramref name="usuarios"/> (HKEY_USERS) e desfaz o quiosque
    /// onde há algo nosso. Um perfil com problema não segura os outros; o que ficou volta na lista.
    /// </summary>
    public static IReadOnlyList<Desfeito> DesfazerNosPerfis(RegistryKey usuarios, IReadOnlyList<string> exesRemovidos, Func<string, bool> existe)
    {
        var feitos = new List<Desfeito>();
        string[] nomes;
        try { nomes = usuarios.GetSubKeyNames(); }
        catch { return feitos; }
        foreach (var sid in nomes)
        {
            if (!DesfazerQuiosque.PerfilDePessoa(sid)) continue;
            try
            {
                using var perfil = usuarios.OpenSubKey(sid);
                if (perfil is null) continue;
                if (DesfazerNoPerfil(sid, perfil, exesRemovidos, existe) is { } feito) feitos.Add(feito);
            }
            catch { /* perfil que nem abre para leitura: não dá para saber se há algo nosso, segue */ }
        }
        return feitos;
    }

    /// <summary>O resumo para a desinstalação: quantos desfeitos e em quantos algo nosso ficou.</summary>
    public static DesfazerQuiosque.Resumo Resumir(IEnumerable<Desfeito> feitos)
    {
        int desfeitos = 0, shell = 0, trava = 0;
        foreach (var f in feitos)
        {
            if (f.AlgumaCoisaSaiu) desfeitos++;
            if (f.ShellFicou) shell++;
            if (f.TravaFicou) trava++;
        }
        return new DesfazerQuiosque.Resumo(desfeitos, shell, trava);
    }

    /// <summary>
    /// Um perfil: lê o Shell, a marca e as quatro políticas, decide pelo núcleo e tira na ordem
    /// (Shell, políticas, marca). Devolve null quando nada ali é nosso (aí nada foi tocado).
    /// </summary>
    public static Desfeito? DesfazerNoPerfil(string sid, RegistryKey perfil, IReadOnlyList<string> exesRemovidos, Func<string, bool> existe)
    {
        var shell = LerShell(perfil);
        var plano = DesfazerQuiosque.Decidir(shell, (chave, valor) =>
        {
            using var k = perfil.OpenSubKey(chave);
            return k?.GetValue(valor, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }, exesRemovidos, existe);
        if (!plano.AlgumaCoisa) return null;

        // 1. o Shell, que é o que evita a tela preta
        var tirouShell = false;
        if (plano.TirarShell)
        {
            try
            {
                using var w = perfil.OpenSubKey(DesfazerQuiosque.ChaveWinlogon, writable: true);
                // relê antes de apagar: só sai o MESMO valor que foi decidido como nosso
                if (w is not null && string.Equals(w.GetValue(DesfazerQuiosque.ValorShell, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, shell, StringComparison.Ordinal))
                    w.DeleteValue(DesfazerQuiosque.ValorShell, throwOnMissingValue: false);
            }
            catch { /* conferido logo abaixo */ }
            tirouShell = ShellSaiu(perfil, shell);
            // O Shell nosso não saiu: a desinstalação vai parar (o Pdv.exe fica), então este perfil fica
            // inteiro como estava, quiosque E trava. Nada pela metade num caixa que continua em uso.
            if (!tirouShell) return new Desfeito(sid, false, 0, false, ShellFicou: true, TravaFicou: false);
        }

        // 2. as políticas, cada uma no seu try: uma que falha não segura as outras
        var tiradas = 0;
        var travaFicou = false;
        foreach (var (chave, valor) in plano.Politicas)
        {
            try
            {
                using var k = perfil.OpenSubKey(chave, writable: true);
                if (k is null) continue;
                k.DeleteValue(valor, throwOnMissingValue: false);
                tiradas++;
            }
            catch { travaFicou = true; }
        }

        // 3. a marca por último, e só com todas as políticas fora: sobrando política, a marca fica
        //    para o próximo Salvar do caixa (ou outra desinstalação) reconhecer o que é nosso
        var tirouMarca = false;
        if (plano.TirarMarca && !travaFicou)
        {
            try
            {
                using var k = perfil.OpenSubKey(TravaDoCtrlAltDel.Marca.Chave, writable: true);
                k?.DeleteValue(TravaDoCtrlAltDel.Marca.Valor, throwOnMissingValue: false);
                tirouMarca = true;
            }
            catch { travaFicou = true; }
        }

        return new Desfeito(sid, tirouShell, tiradas, tirouMarca, ShellFicou: false, travaFicou);
    }

    /// <summary>O Shell do perfil como está gravado (sem expandir variáveis), ou null.</summary>
    private static string? LerShell(RegistryKey perfil)
    {
        using var w = perfil.OpenSubKey(DesfazerQuiosque.ChaveWinlogon);
        return w?.GetValue(DesfazerQuiosque.ValorShell, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>Relê o Shell depois de tentar apagar: saiu só se o valor decidido não está mais lá. Leitura que lança conta como "ficou".</summary>
    private static bool ShellSaiu(RegistryKey perfil, string? decidido)
    {
        try { return !string.Equals(LerShell(perfil), decidido, StringComparison.Ordinal); }
        catch { return false; }
    }
}
