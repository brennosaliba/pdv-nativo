using Microsoft.Data.Sqlite;
using Pdv.Nucleo;

namespace Pdv.Testes;

/// <summary>
/// TESTE NENHUM PODE FALAR COM A NUVEM DE VERDADE.
///
/// O QUE ACONTECEU (09/09/2026). O dono abriu a lista de terminais do painel e viu
/// SEIS caixas na Savassi, três deles chamados "Caixa", com turno aberto. A loja
/// não tem seis caixas: três daquelas linhas eram `term-teste`, `term-promo` e
/// `term-valor`, semeados pela suíte de testes desta pasta.
///
/// COMO ELES CHEGARAM LÁ, que é a parte que interessa:
///
///   1. o teste cria um banco SQLite temporário e insere uma linha em `terminal`
///      com `api_base = http://127.0.0.1:9`, uma porta morta de propósito;
///   2. só que `AtualizarCaixa.ConsultarPainelAsync` NÃO lê `api_base` (essa coluna
///      não é lida em lugar nenhum do núcleo). Ela lê `config.supabase_url`;
///   3. o banco temporário não tem essa chave, então o código cai no padrão, que
///      é o projeto de PRODUÇÃO (`Nuvem.UrlPadrao`);
///   4. as credenciais, essas são da MÁQUINA (ProtectedData), não do banco. A
///      máquina de quem roda os testes tem credencial de verdade;
///   5. a tela de venda que o teste sobe tem um relógio que pergunta a versão ao
///      painel. Ele perguntou. Com o uuid de teste. Para a produção.
///
/// Não é sujeira cosmética: terminal fantasma entra na frota, recebe política de
/// atualização, escreve `ultimo_detalhe`, e faz a loja parecer ter turno aberto
/// onde não há ninguém.
///
/// A CORREÇÃO É DE UMA LINHA E MORA AQUI: todo teste que semeia um terminal
/// aponta a nuvem para um endereço morto ANTES de subir qualquer tela. Quem
/// vigia se alguém esqueceu é <see cref="TestesIsolamento"/>, que lê os fontes
/// desta pasta.
/// </summary>
public static class Isolamento
{
    /// <summary>
    /// Endereço que não existe e não vai existir. `127.0.0.1:9` é a porta
    /// "discard" em loopback: recusa na hora, sem timeout de 30 s em cada teste.
    /// </summary>
    public const string UrlMorta = "http://127.0.0.1:9";

    /// <summary>
    /// Corta o caminho do teste para qualquer backend real.
    ///
    /// Chame logo depois de inserir a linha de `terminal`, e antes de subir tela.
    /// Sem `supabase_url` gravada, `ConsultarPainelAsync` usa o padrão, que é a
    /// produção.
    /// </summary>
    public static void SemNuvem(SqliteConnection cx)
    {
        Vendas.GravarConfig(cx, "supabase_url", UrlMorta);
        // A url de atualização é a outra porta para fora: o arquivo público
        // versao.json. Ela não cria terminal fantasma, mas um teste que baixa
        // 175 MB de instalador sem querer também não serve a ninguém.
        Vendas.GravarConfig(cx, "atualizacao_url", UrlMorta + "/pdv/versao.json");
    }
}
