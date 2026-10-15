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
/// Só mexe em HKEY_USERS\SID (nunca no HKLM nem no HKCU de quem roda o desinstalador), e a raiz vem
/// de fora: HKEY_USERS na loja, uma chave isolada na bateria.
/// </summary>
public static class QuiosqueDoCaixa
{
    /// <summary>Um perfil em que o quiosque foi desfeito, e quantas políticas da trava saíram.</summary>
    public sealed record Desfeito(string Sid, int Politicas);

    /// <summary>
    /// Percorre os perfis embaixo de <paramref name="usuarios"/> (HKEY_USERS) e desfaz o quiosque
    /// onde o Shell é do caixa. Um perfil com problema não segura os outros nem a desinstalação.
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
                if (DesfazerNoPerfil(perfil, exesRemovidos, existe) is { } tiradas) feitos.Add(new Desfeito(sid, tiradas));
            }
            catch { /* perfil com problema: os outros seguem, e a desinstalação também */ }
        }
        return feitos;
    }

    /// <summary>
    /// Um perfil: lê o Shell e as quatro políticas, decide pelo núcleo, tira as políticas da trava e
    /// por ÚLTIMO o Shell (se algo falhar no meio, rodar de novo ainda acha o Shell). Devolve quantas
    /// políticas tirou, ou null quando o Shell não é do caixa (aí nada foi tocado).
    /// </summary>
    public static int? DesfazerNoPerfil(RegistryKey perfil, IReadOnlyList<string> exesRemovidos, Func<string, bool> existe)
    {
        var shell = LerShell(perfil);
        var plano = DesfazerQuiosque.Decidir(shell, (chave, valor) =>
        {
            using var k = perfil.OpenSubKey(chave);
            return k?.GetValue(valor, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }, exesRemovidos, existe);
        if (!plano.TirarShell) return null;

        var tiradas = 0;
        foreach (var (chave, valor) in plano.Politicas)
        {
            using var k = perfil.OpenSubKey(chave, writable: true);
            if (k is null) continue;
            k.DeleteValue(valor, throwOnMissingValue: false);
            tiradas++;
        }

        using (var w = perfil.OpenSubKey(DesfazerQuiosque.ChaveWinlogon, writable: true))
        {
            // relê antes de apagar: só sai o MESMO valor que foi decidido como nosso
            if (w is not null && string.Equals(w.GetValue(DesfazerQuiosque.ValorShell, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, shell, StringComparison.Ordinal))
                w.DeleteValue(DesfazerQuiosque.ValorShell, throwOnMissingValue: false);
        }
        return tiradas;
    }

    /// <summary>O Shell do perfil como está gravado (sem expandir variáveis), ou null.</summary>
    private static string? LerShell(RegistryKey perfil)
    {
        using var w = perfil.OpenSubKey(DesfazerQuiosque.ChaveWinlogon);
        return w?.GetValue(DesfazerQuiosque.ValorShell, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}
