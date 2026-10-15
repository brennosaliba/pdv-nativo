# Só o PDV no Windows (quiosque) e Música da loja (Spotify) — 1.0.9, 12/09/2026

Dois pedidos do dono na madrugada de 12/09:

- "tem como adicionar o PDV como serviço, iniciar no startup e aparecer somente ele no
  Windows? nem carregar mais nada, nem Spotify nem nada, somente o PDV"
- "limitar a música da playlist do Spotify: eu escolho daqui (painel) o que vai tocar
  lá, e no PDV ter controle também"

## 1. Só o PDV no Windows (modo quiosque)

Serviço do Windows não serve: serviço não tem tela. O que resolve é o PDV virar o
**shell** do usuário do caixa. O Windows entra e, em vez da área de trabalho, da barra
e dos programas de inicialização, abre o PDV. Nada mais carrega.

### Como ligar (no caixa)

1. Configuração (senha de administrador) → bloco **SÓ O PDV NO WINDOWS (QUIOSQUE)**.
2. Marque "Abrir o Windows direto no PDV..." e toque em **Salvar alterações**.
3. Para o caixa ligar sozinho: no Windows, `Win + R` → `netplwiz` → desmarque
   "Os usuários devem digitar um nome de usuário e senha" → informe a senha do usuário
   do caixa uma vez. (Isso é do dono; o PDV nunca mexe em senha do Windows.)
4. Reinicie o PC: ele entra direto no PDV.

### O que muda no dia a dia

- **Fechar / Sair** no caixa pergunta: **Reiniciar o PDV** ou **Sair para o Windows**
  (abre a área de trabalho). Nunca deixa a tela preta.
- **Atualizar** continua igual: o instalador troca o programa e reabre o PDV.
- Se o PDV cair por um erro grave, ele abre a área de trabalho antes de fechar (o dono
  tem por onde mexer).
- Na Configuração há o botão **Abrir a área de trabalho do Windows agora** (o PDV
  continua aberto; a área de trabalho aparece atrás).

### Como desligar

Configuração → desmarque a opção → Salvar. Na próxima entrada o Windows volta ao normal.
(Ou, por fora: apague o valor `Shell` em
`HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Winlogon`.)

### Por dentro

`Quiosque.cs`: grava `"C:\Program Files\MMFood\Pdv.exe" --quiosque` em
`HKCU\...\Winlogon\Shell` (registro do usuário; essa chave o usuário controla, então
grava sem UAC). `Ligado` lê o registro (o registro é a verdade, não o banco).
`Desligar` só apaga se o valor for o nosso. Nunca toca em `HKLM`, `AutoAdminLogon` ou
`DefaultPassword` (a suíte vigia isso).

### A trava do Ctrl+Alt+Del (1.0.33; corrigida na 1.0.36, 14/10/2026)

A opção "Travar o Ctrl+Alt+Del" grava quatro políticas do usuário:
`Policies\System` (`DisableTaskMgr`, `DisableLockWorkstation`, `DisableChangePassword`)
e `Policies\Explorer` (`NoLogoff`), todas DWORD 1. Na tela do Ctrl+Alt+Del sobram
Cancelar e o botão de desligar.

A 1.0.33 achava que essas chaves gravavam sem UAC, como o `Shell`. Não gravam. A chave
`HKCU\Software\Microsoft\Windows\CurrentVersion\Policies` é protegida: o usuário só lê,
e só o SYSTEM e os Administradores **elevados** escrevem. Com o UAC ligado (o padrão do
Windows) a Savassi viu "Access to the registry key ... is denied" e a trava nunca gravou.

Como funciona desde a 1.0.36:

1. Configuração → marque (ou desmarque) a trava → **Salvar alterações**.
2. O PDV tenta gravar direto. Dá certo só com o UAC desligado ou com o PDV aberto como
   administrador; aí não aparece aviso nenhum.
3. Negado, o PDV pede ao Windows para rodar como administrador **um programa do próprio
   Windows**: o `cmd.exe` de `C:\Windows\System32`, com quatro `reg.exe` (também de
   System32) que gravam (ou apagam) as quatro políticas em `HKEY_USERS\<SID do caixa>`.
   O Windows escurece a tela e mostra **Processador de Comandos do Windows**, editor
   verificado **Microsoft Windows**. Usuário administrador toca em **Sim**; usuário comum
   digita a senha de um administrador. Um aviso só para as quatro políticas.
4. `HKEY_USERS\<SID>` e não `HKCU`: com usuário comum quem digita a senha é outra conta, e
   o HKCU do processo elevado seria o dela. O SID vem da conta do processo do PDV (nunca de
   arquivo nem de texto digitado) e passa por um formato estrito; a linha do cmd é montada
   só de constantes e desse SID (`cmd /d /v:off /s /c "...reg.exe add ... /f & ..."`; o
   `/d` desliga o AutoRun, que o usuário consegue gravar).
5. O PDV espera o cmd até 20 s e **relê o registro** (o código de saída de um processo
   elevado não volta para quem pediu). Deu certo: nada na tela. Responderam Não: "Sem a
   permissão do Windows, a trava do Ctrl+Alt+Del não foi ligada. O resto foi salvo." O
   detalhe técnico vai só para a auditoria (`ctrl_alt_del_travado`, `_destravado`,
   `_recusado`, `_falhou`).

**Por que não o próprio Pdv.exe (revisão de segurança da 1.0.36).** O rascunho da 1.0.36
relançava o `Pdv.exe --travas on|off --sid SID` como administrador. O Pdv.exe é publicado em
arquivo único e extrai bibliotecas nativas numa pasta que o usuário grava (`%TEMP%\.net`):
elevar o Pdv.exe seria dar administrador a código que um usuário comum consegue plantar. O
modo `--travas` foi removido; o Pdv.exe não tem modo de administrador nenhum.

A abertura do PDV **nunca** pede permissão ao Windows. Se o quiosque foi desligado por fora
e a trava ficou, ela tenta tirar direto e, sem permissão, deixa `ctrl_alt_del_ficou` na
auditoria; quem tira é a Configuração (desmarcar e salvar, com o aviso do Windows).

Para conferir no PC da loja (cmd aberto como o usuário do caixa, sem "Executar como
administrador": o cmd elevado de outra conta lê o HKCU dela):

```
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System"
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
```

Por dentro: regra pura em `Pdv.Nucleo/TravaDoCtrlAltDel.cs` (SID, pasta do sistema, linha do
cmd, ordem das tentativas, frases); registro e processo em `Quiosque.cs` (`GravarTravas`,
`TentarDireto`, `MudarTrava`, `PedirAoWindows`). A bateria grava só numa chave isolada
(`HKCU\Software\PdvNativoTeste\<guid>`), apagada no fim, e roda a linha de verdade num cmd
comum (sem administrador) contra essa chave.

### Desinstalar desfaz o quiosque (1.0.36)

Desinstalar apagava o Pdv.exe e deixava no registro do usuário do caixa o `Shell` apontando
para ele e as quatro políticas da trava: na próxima entrada, tela preta e um Ctrl+Alt+Del só
com Cancelar. Agora o desinstalador (que já roda como administrador), **antes** de apagar o
programa, percorre os perfis carregados em `HKEY_USERS` e, onde o `Shell` é o do PDV
(`"...\Pdv.exe" --quiosque` apontando para o Pdv.exe que sai, ou para um que já não existe),
apaga as políticas da trava que estão em DWORD 1 e depois o `Shell`. Shell de outro programa,
política em 0 ou de outro tipo, `.DEFAULT`, SYSTEM e os perfis sem o nosso Shell ficam como
estão. A mensagem final diz "O modo quiosque foi desfeito" quando houve.

O instalador não sabe quem é o usuário do caixa, por isso olha os perfis **carregados**:
desinstale com o usuário do caixa logado (pelo botão "Sair para o Windows" da Configuração).
Perfil não carregado (o caixa deslogado) não é tocado.

Por dentro: regra pura em `Pdv.Nucleo/DesfazerQuiosque.cs` (o mesmo formato de Shell que o
`Quiosque.cs` grava), registro em `Pdv.Instalador/QuiosqueDoCaixa.cs`, chamada no começo de
`Instalacao.Desinstalar`.

## 2. Música da loja (Spotify)

### O caixa não aparece na lista de aparelhos (1.0.10, 12/09/2026)

O dono conectou a conta e viu na lista só o PC do escritório. Não é o Client ID:
o Client ID identifica o app, a conta é uma só para a empresa, e a lista de
aparelhos é a do Spotify: aparece quem está com o app aberto e logado naquela
conta. O PC do escritório aparecia porque o Spotify estava aberto nele; o caixa
não aparecia porque no quiosque nada mais carrega.

A saída, sem perder o quiosque: **Configuração → bloco do quiosque → "Abrir o
Spotify escondido atrás do PDV"**. Com a opção ligada, o PDV abre o app do Spotify
ao iniciar (com `--minimized`) e uma vigia, a cada 30 s, minimiza a janela se ela
aparecer e reabre o programa se ele morrer (no máximo uma vez a cada 2 minutos).
Na tela continua só o PDV, e o caixa entra na lista com o nome deste computador
(`DESKTOP-7AJ1OD7` na Savassi). A janela **♫ música** do caixa diz se este caixa
está na lista de aparelhos, e o que fazer se não estiver.

Pré-requisito, feito uma vez pelo dono no PC da loja: instalar o Spotify (versão
da Loja ou instalador clássico) e entrar com a conta da empresa. O PDV nunca mexe
em senha, nunca mata o Spotify; só abre e esconde. Por dentro: `SpotifyNoCaixa.cs`
(raiz), `Spotify.AparelhosAsync` (GET /me/player/devices), config `spotify_escondido`.


### O desenho

- O **painel** (ERP → PDV → **Música**) conecta a conta Premium da empresa uma vez,
  escolhe a **playlist da loja** e o **aparelho** onde toca. Só o painel escolhe.
- O **caixa** (rodapé, botão **♫ música**) mostra o que está tocando e tem: tocar a
  playlist da loja, pausar/continuar, anterior, próxima e volume. Nada de escolher outra
  playlist: "eu escolho daqui o que vai tocar lá".
- Quem **toca o som** é um aparelho com o Spotify aberto e logado nessa conta: caixa de
  som com Spotify Connect, celular/tablet da loja, ou um PC com o app do Spotify. O
  caixa e o painel só mandam (Spotify Connect). No modo quiosque o PC do caixa não roda
  o Spotify, então o aparelho é outro (o normal numa loja: a caixa de som).
- Por que o caixa não toca ele mesmo: o Web Playback SDK do Spotify exige Widevine, e o
  WebView2 desta máquina não tem (sonda `wv2exp Drm` em 12/09: PlayReady OK, Widevine
  nunca respondeu). Premium é exigência do Spotify para controlar a reprodução.

### Passo a passo (uma vez, o dono)

1. `developer.spotify.com/dashboard` com a conta Premium → **Create app**.
   - Redirect URI: `https://erp.americandaybrasil.com.br/pdv/musica` (a página mostra o
     valor exato para copiar).
   - Marque **Web API**. Copie o **Client ID**.
2. ERP → PDV → **Música** → cole o Client ID → **Guardar Client ID** → **Conectar
   Spotify** → autorize.
3. Escolha a **playlist da loja** e o **aparelho** (o aparelho precisa estar com o
   Spotify aberto para aparecer na lista).
4. No caixa: **Atualizar** (puxa playlist e aparelho) → **♫ música** no rodapé.

### Segurança

- O `refresh_token` do Spotify fica em `spotify_conta`, tabela sem policy nenhuma: pela
  API ninguém lê nem escreve. Só a função de borda `spotify` (chave de serviço) renova
  o token. Painel e caixa recebem `access_token` de 1 h.
- O caixa chama a função com a sessão do terminal (papel `store`); a função confere o
  papel no banco, nunca no corpo do pedido.
- PKCE, sem client secret: nada de segredo do app no navegador nem no exe.

### Por dentro

- ERP: `src/pdv/lib/spotify.ts` (PKCE, token com cache, Web API, `pdv_musica`),
  `src/pdv/paginas/Musica.tsx`, função `supabase/functions/spotify/index.ts`, migration
  `20260912010000_musica_e_config_caixa.sql` (`pdv_musica`, `spotify_conta`, RPC
  `pdv_loja_config_caixa`).
- PDV: `Pdv.Nucleo/Spotify.cs` (leitura pura das respostas + chamadas),
  `Nuvem.TokenSpotifyAsync` / `Nuvem.FuncaoAsync`, `Telas/Musica.cs` (janela),
  `ConfigLojaPainel` (playlist/aparelho/volume chegam no Atualizar).

## 3. Config da loja pelo painel (respostas prontas e senha de administrador)

ERP → PDV → **Configurações** (da loja) ganhou dois cartões:

- **Respostas prontas do chat do iFood**: mesmo formato do caixa (um bloco por
  resposta, linha em branco separa, primeira linha é o título). Vazio = o caixa mantém
  as respostas que já tem (ou as de fábrica).
- **Senha de administrador do caixa**: 4 a 6 números, vira hash no navegador (PBKDF2
  igual ao do PDV, provado por vetor de teste contra o .NET). O caixa aplica no
  próximo **Atualizar**, e só quando a senha do painel for mais nova que a última
  aplicada (o ciclo de sincronização passa a toda hora; a troca de senha é um ato).

Tudo vem pela RPC `pdv_loja_config_caixa` (uma linha por loja ao alcance do usuário; o
caixa escolhe a da loja do terminal pelo nome). Regras em `Pdv.Nucleo/ConfigLojaPainel`,
provadas num SQLite real na suíte (`TestesConfigLojaPainel`).
