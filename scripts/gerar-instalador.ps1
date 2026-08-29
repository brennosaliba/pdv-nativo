#Requires -Version 5.1
<#
    GERA O INSTALADOR FINAL — um arquivo só, para a loja baixar e abrir.

    Uso:
        .\scripts\gerar-instalador.ps1                     # usa o publish mais novo
        .\scripts\gerar-instalador.ps1 -PastaPdv publish\v20
        .\scripts\gerar-instalador.ps1 -SemPayGo           # só o caixa

    O que sai:  publish\InstalarPdv.exe  (~236 MB)

    COMO FUNCIONA, em duas linhas: publica o InstalarPdv autocontido (para abrir num
    PC de loja recém formatado, que não tem .NET nenhum) e depois chama esse mesmo exe
    com --empacotar, que pendura a pasta do PDV e o paygo.exe na cauda dele. Quem grava
    o formato do pacote é o mesmo código que o lê, em Pdv.Instalador\Pacote.cs.

    ⚠️ ESTE ARQUIVO PRECISA DE BOM UTF-8. O PowerShell 5.1 lê .ps1 sem BOM como ANSI,
       e aí os acentos quebram as aspas e o script morre com erro de sintaxe que não
       tem nada a ver com o que está escrito.
#>
[CmdletBinding()]
param(
    [string] $PastaPdv,
    [string] $PayGo,
    [string] $Saida,
    [switch] $SemPayGo
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Passo([string] $t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Morre([string] $t) { Write-Host "ERRO: $t" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- 1. o payload
if (-not $PastaPdv) {
    # O publish mais novo por número de versão, não por data: renomear pasta ou
    # restaurar backup mexe na data e faria o script empacotar uma versão velha
    # sem ninguém perceber.
    $cands = Get-ChildItem (Join-Path $raiz 'publish') -Directory -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -match '^v(\d+)$' -and (Test-Path (Join-Path $_.FullName 'Pdv.exe')) } |
             Sort-Object { [int]($_.Name -replace '^v', '') } -Descending
    if (-not $cands) { Morre "nao achei nenhuma pasta publish\vNN com Pdv.exe dentro." }
    $PastaPdv = $cands[0].FullName
}
if (-not (Test-Path $PastaPdv)) { Morre "pasta do PDV nao existe: $PastaPdv" }
$PastaPdv = (Resolve-Path $PastaPdv).Path
Passo "PDV: $PastaPdv"

if (-not $PayGo -and -not $SemPayGo) { $PayGo = Join-Path $raiz 'publish\paygo.exe' }
if ($SemPayGo) {
    $PayGo = $null
    Write-Host "    (sem PayGo: o caixa instala, o cartao fica para depois)" -ForegroundColor Yellow
} elseif (-not (Test-Path $PayGo)) {
    Morre "nao achei o paygo em $PayGo. Use -SemPayGo se for de proposito."
} else {
    $PayGo = (Resolve-Path $PayGo).Path
    Passo "PayGo: $PayGo"
}

if (-not $Saida) { $Saida = Join-Path $raiz 'publish\InstalarPdv.exe' }

# ------------------------------------------------------- 2. publicar o casulo
$obj = Join-Path $raiz 'publish\_instalador'
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }

Passo 'Publicando o instalador (autocontido, arquivo unico)...'
# As flags de autocontido/arquivo-unico moram no csproj, nao aqui — de proposito:
# publicar sem elas devolve um instalador que morre com "instale o .NET" na loja.
& dotnet publish (Join-Path $raiz 'Pdv.Instalador\Pdv.Instalador.csproj') `
    -c Release -o $obj --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Morre "o publish do instalador falhou (codigo $LASTEXITCODE)." }

$casulo = Join-Path $obj 'InstalarPdv.exe'
if (-not (Test-Path $casulo)) { Morre "o publish nao produziu InstalarPdv.exe." }

# Conferencia que ja salvou o projeto uma vez: se as .dll nativas do WPF sairam
# soltas, o "arquivo unico" nao e unico e copiar so o exe entrega algo que nao abre.
$soltas = Get-ChildItem $obj -Filter *.dll -File
if ($soltas) {
    Morre ("o publish deixou " + $soltas.Count + " .dll soltas ao lado do exe — " +
           "IncludeNativeLibrariesForSelfExtract nao pegou. Nao vou empacotar assim.")
}

# ---------------------------------------------------------- 3. pendurar a cauda
# ⚠️ Quem empacota e o Pdv.Testes, NAO o proprio InstalarPdv.exe. O instalador tem
# requireAdministrator no manifesto, e o Windows recusa inicia-lo de um shell comum
# ("a operacao solicitada requer elevacao") — empacotar e passo de build e nao pode
# pedir UAC. O codigo do formato e o mesmo: Pdv.Instalador\Pacote.cs entra no
# Pdv.Testes por <Compile Include>.
Passo 'Compilando a ferramenta de empacotar...'
& dotnet build (Join-Path $raiz 'Pdv.Testes\Pdv.Testes.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Morre "o build do Pdv.Testes falhou (codigo $LASTEXITCODE)." }

$ferramenta = Join-Path $raiz 'Pdv.Testes\bin\Release\net8.0-windows\Pdv.Testes.exe'
if (-not (Test-Path $ferramenta)) { Morre "nao achei $ferramenta" }

# ⚠️ CHAMA O EXE DIRETO, nao `dotnet run -- ...`. O PowerShell 5.1 come o `--` na
# passagem para comando nativo: os argumentos nao chegaram ao app, o modo nao casou e
# o que rodou foi a SUITE DE TESTES inteira, com o script achando que empacotou.
Passo 'Empacotando o PDV e o PayGo na cauda...'
$argPayGo = if ($PayGo) { $PayGo } else { '-' }
& $ferramenta --empacotar $casulo $PastaPdv $argPayGo $Saida | Write-Host
if ($LASTEXITCODE -ne 0) { Morre "o empacotamento falhou (codigo $LASTEXITCODE)." }

# ------------------------------------------------------------- 4. conferencia
if (-not (Test-Path $Saida)) { Morre "o instalador nao foi gravado em $Saida." }

# Abre de verdade o que acabou de sair: extrai a cauda num temporario e confere que o
# PDV la dentro esta completo. Ler o trailer prova que a conta fecha; descompactar
# prova que o zip esta inteiro — e essa e a diferenca entre descobrir aqui e descobrir
# com a loja parada.
Passo 'Conferindo o instalador gerado (abre e extrai de verdade)...'
& $ferramenta --conferir-pacote $Saida | Write-Host
if ($LASTEXITCODE -ne 0) { Morre "o instalador gerado nao passou na conferencia." }
$mb = [math]::Round((Get-Item $Saida).Length / 1MB, 1)

Write-Host ''
Write-Host "PRONTO: $Saida  ($mb MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'Na loja: baixe este unico arquivo e abra. Ele instala o caixa, instala o'
Write-Host 'PayGo e abre a configuracao passo a passo. Nao precisa de mais nada junto.'
Write-Host ''
Write-Host 'ATENCAO: o exe nao e assinado digitalmente. O SmartScreen do Windows vai'
Write-Host 'avisar na primeira abertura ("Mais informacoes" -> "Executar assim mesmo").'
Write-Host 'Assinatura e cauda nao convivem no desenho atual: assinar ANTES e depois'
Write-Host 'anexar invalida a assinatura (bytes novos no fim); assinar DEPOIS poe o'
Write-Host 'bloco de certificado no fim e o trailer deixa de ser os ultimos 32 bytes.'
Write-Host 'Se um dia houver certificado, ver o comentario no topo de Pacote.cs.'
