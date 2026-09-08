#Requires -Version 5.1
<#
    MONTA A PASTA DO AGENTE FISCAL para o instalador pendurar na cauda.

    Uso:
        .\scripts\montar-agente.ps1
        .\scripts\montar-agente.ps1 -Fonte C:\caminho\ec2-sefaz-server -Node C:\node\node.exe

    O que sai:  publish\agent\  (~97 MB: codigo + bibliotecas + node.exe)

    POR QUE ISTO EXISTE. O agente e quem emite nota com a internet caida. Ate 08/09/2026
    ninguem o instalava: a loja so tinha contingencia se alguem tivesse copiado a pasta
    na mao, e loja sem ele, sem internet, vende sem nota nenhuma.

    O QUE ENTRA, e por que nao e a pasta inteira:
      . pdv-agent.cjs e os .cjs de nfce\ que ele carrega. Os test-*.cjs e o demo-*.cjs
        ficam de fora: sao ferramenta de quem desenvolve.
      . server.cjs NAO entra. Ele e o servidor da EC2, nao o agente do caixa.
      . node_modules: so o FECHO das dependencias de verdade (axios, fast-xml-parser,
        node-forge, xml-crypto e o que elas puxam). Sao 5 MB; a pasta inteira tem 82,
        porque carrega o express e o resto que so o server.cjs usa.
      . node.exe, o runtime. E ele que pesa (92 MB, 33 no zip).

    ⚠️ ESTE ARQUIVO PRECISA DE BOM UTF-8 (ver gerar-instalador.ps1).
#>
[CmdletBinding()]
param(
    [string] $Fonte,
    [string] $Node,
    [string] $Saida
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Passo([string] $t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Morre([string] $t) { Write-Host "ERRO: $t" -ForegroundColor Red; exit 1 }

if (-not $Fonte) { $Fonte = Join-Path (Split-Path -Parent $raiz) 'erp-american-day\ec2-sefaz-server' }
if (-not (Test-Path (Join-Path $Fonte 'pdv-agent.cjs'))) {
    Morre "nao achei pdv-agent.cjs em $Fonte. Passe -Fonte com a pasta do agente."
}
$Fonte = (Resolve-Path $Fonte).Path
Passo "Fonte: $Fonte"

if (-not $Node) {
    $Node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $Node) { $Node = 'C:\node\node.exe' }
}
if (-not (Test-Path $Node)) { Morre "nao achei o node.exe. Passe -Node com o caminho." }
$Node = (Resolve-Path $Node).Path
Passo "Runtime: $Node"

if (-not $Saida) { $Saida = Join-Path $raiz 'publish\agent' }
if (Test-Path $Saida) { Remove-Item $Saida -Recurse -Force }
New-Item -ItemType Directory -Force $Saida | Out-Null

# ------------------------------------------------------------------ 1. codigo
Passo 'Copiando o codigo do agente...'
Copy-Item (Join-Path $Fonte 'pdv-agent.cjs') $Saida
$destNfce = Join-Path $Saida 'nfce'
New-Item -ItemType Directory -Force $destNfce | Out-Null
Get-ChildItem (Join-Path $Fonte 'nfce') -Filter *.cjs -File |
    Where-Object { $_.Name -notlike 'test-*' -and $_.Name -notlike 'demo-*' } |
    ForEach-Object { Copy-Item $_.FullName $destNfce }

# ------------------------------------------------------- 2. as bibliotecas
# O FECHO e calculado pelo proprio node, lendo os package.json: lista escrita a mao
# envelhece calada, e o sintoma seria o agente morrendo no primeiro require em loja.
Passo 'Calculando o fecho das dependencias...'
$fechoJs = @'
const fs=require('fs'),path=require('path');
const raiz=process.argv[2], vistos=new Set();
function deps(nome){
  if(vistos.has(nome))return; vistos.add(nome);
  const p=path.join(raiz,nome,'package.json');
  if(!fs.existsSync(p))return;
  const j=JSON.parse(fs.readFileSync(p,'utf8'));
  for(const d of Object.keys(j.dependencies||{})) deps(d);
}
['axios','fast-xml-parser','node-forge','xml-crypto'].forEach(deps);
console.log([...vistos].filter(n=>fs.existsSync(path.join(raiz,n))).join('\n'));
'@
$fechoArq = Join-Path $env:TEMP ('fecho-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.cjs')
Set-Content -Path $fechoArq -Value $fechoJs -Encoding utf8
try {
    $pacotes = & $Node $fechoArq (Join-Path $Fonte 'node_modules')
    if ($LASTEXITCODE -ne 0) { Morre "nao consegui calcular o fecho das dependencias." }
} finally { Remove-Item $fechoArq -Force -ErrorAction SilentlyContinue }

$pacotes = @($pacotes | Where-Object { $_ -and $_.Trim() })
if ($pacotes.Count -lt 4) { Morre "o fecho voltou com $($pacotes.Count) pacotes: alguma coisa esta errada." }
Passo "Bibliotecas: $($pacotes.Count) pacotes"

$destMods = Join-Path $Saida 'node_modules'
foreach ($p in $pacotes) {
    $de = Join-Path (Join-Path $Fonte 'node_modules') $p
    $para = Join-Path $destMods $p
    New-Item -ItemType Directory -Force (Split-Path -Parent $para) | Out-Null
    Copy-Item $de $para -Recurse -Force
}

# ------------------------------------------------------------------ 3. runtime
Passo 'Copiando o runtime...'
Copy-Item $Node (Join-Path $Saida 'node.exe')

# --------------------------------------------------------------- 4. conferir
# A mesma pergunta que o Pdv.Instalador vai fazer no build (ConferirOrigemAgente):
# descobrir aqui e barato, descobrir na loja e com a internet caida.
foreach ($obrigatorio in @('pdv-agent.cjs', 'node.exe', 'nfce\xml.cjs', 'node_modules\axios\package.json')) {
    if (-not (Test-Path (Join-Path $Saida $obrigatorio))) { Morre "faltou $obrigatorio na pasta montada." }
}

$mb = [math]::Round(((Get-ChildItem $Saida -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
$n  = (Get-ChildItem $Saida -Recurse -File).Count

Write-Host ''
Write-Host "PRONTO: $Saida  ($n arquivos, $mb MB)" -ForegroundColor Green
Write-Host 'Agora rode scripts\gerar-instalador.ps1' -ForegroundColor Green
Write-Host ''
