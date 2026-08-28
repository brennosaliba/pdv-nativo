# gravar-chave-tef.ps1 — grava a CHAVE DE INTEGRAÇÃO do ControlPay no cofre da máquina.
#
# Por que existe: a chave vive cifrada por DPAPI (escopo LocalMachine) em
# C:\ProgramData\PdvNativo\seg\seg.dat — é de onde Servicos.Tef() lê. Ela NÃO vai
# para o Supabase, nem para o banco `config`, nem para o git: é credencial DESTA
# máquina (copiar o arquivo para outro PC não abre).
#
# O token é lido por prompt oculto: não aparece na tela, não fica no histórico do
# PowerShell e não passa por arquivo texto em nenhum momento.
#
# Uso (PowerShell normal, com o PDV FECHADO):
#   powershell -ExecutionPolicy Bypass -File scripts\gravar-chave-tef.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$pasta   = 'C:\ProgramData\PdvNativo\seg'
$arq     = Join-Path $pasta 'seg.dat'
$backup  = Join-Path $pasta ('seg.dat.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

# 1) O PDV reescreve o cofre INTEIRO quando você salva a Configuração. Se ele
#    estiver aberto, o que este script gravar seria sobrescrito sem aviso.
$proc = Get-Process -Name 'Pdv' -ErrorAction SilentlyContinue
if ($proc) { throw 'O PDV está aberto. Feche-o antes de rodar este script.' }
if (-not (Test-Path $arq)) { throw "Cofre não encontrado em $arq — configure o PDV pela tela primeiro." }

# 2) Lê o cofre atual. Preservar as outras chaves é obrigatório: aqui moram a
#    senha do certificado, o CSC e a credencial da nuvem — perder qualquer uma
#    quebra a emissão fiscal.
$bytes  = [IO.File]::ReadAllBytes($arq)
$claro  = [Text.Encoding]::UTF8.GetString(
            [Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, 'LocalMachine'))
$cofre  = @{}
(ConvertFrom-Json $claro).PSObject.Properties | ForEach-Object { $cofre[$_.Name] = $_.Value }

Write-Host "Cofre atual ($($cofre.Count) chaves):" -ForegroundColor Cyan
$cofre.Keys | Sort-Object | ForEach-Object { Write-Host ("  {0} ({1} caracteres)" -f $_, $cofre[$_].Length) }

Copy-Item $arq $backup
Write-Host "Backup: $backup" -ForegroundColor DarkGray

function Ler-Segredo($rotulo) {
    $sec = Read-Host -AsSecureString $rotulo
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

# 3) Token. Colar com o botão direito do mouse (o campo fica em branco de propósito).
$chave = (Ler-Segredo 'Cole a CHAVE DE INTEGRACAO do ControlPay').Trim()
if ($chave.Length -eq 0) { throw 'Chave vazia — nada foi alterado.' }
# a tela do PDV faz o mesmo: chave copiada do portal às vezes vem percent-encoded
if ($chave.Contains('%')) { try { $chave = [Uri]::UnescapeDataString($chave) } catch {} }
$cofre['cpayChave'] = $chave

# 4) Senha técnica (Enter mantém a que já está lá; padrão PayGo = 314159).
$tec = (Ler-Segredo 'Senha TECNICA do ControlPay (Enter = manter a atual)').Trim()
if ($tec.Length -gt 0) { $cofre['cpaySenhaTecnica'] = $tec }

# 5) Grava no mesmo formato que o PDV lê: JSON UTF-8 + DPAPI LocalMachine.
$json = ($cofre | ConvertTo-Json -Compress)
[IO.File]::WriteAllBytes($arq,
  [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($json), $null, 'LocalMachine'))

# 6) Relê do disco e prova que o PDV vai conseguir abrir — sem imprimir valor nenhum.
$conf = @{}
(ConvertFrom-Json ([Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($arq), $null, 'LocalMachine')
))).PSObject.Properties | ForEach-Object { $conf[$_.Name] = $_.Value }

Write-Host ''
if ($conf['cpayChave'] -eq $chave -and $conf.Count -ge $cofre.Count) {
    Write-Host 'OK — chave gravada e conferida no cofre.' -ForegroundColor Green
    $conf.Keys | Sort-Object | ForEach-Object { Write-Host ("  {0} ({1} caracteres)" -f $_, $conf[$_].Length) }
    Write-Host ''
    Write-Host 'Agora abra o PDV -> Configuracao -> TEF e clique "Testar ControlPay".' -ForegroundColor Cyan
} else {
    Copy-Item $backup $arq -Force
    throw "Conferencia falhou — cofre restaurado do backup ($backup)."
}
