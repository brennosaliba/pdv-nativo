# senha-admin.ps1 — troca a SENHA DE ADMINISTRADOR do PDV (a que abre a tela de
# Configuração e autoriza sangria/cancelamento).
#
# A senha é guardada como PBKDF2-SHA256 (100.000 iterações, sal de 16 bytes,
# hash de 32) em base64, exatamente como Pdv.Nucleo.Operadores.GerarHash faz —
# a senha em si não é gravada em lugar nenhum e não aparece na tela.
#
# Uso (PowerShell, com o PDV FECHADO):
#   powershell -ExecutionPolicy Bypass -File scripts\senha-admin.ps1

$ErrorActionPreference = 'Stop'

$db = 'C:\ProgramData\PdvNativo\pdv.db'
if (-not (Test-Path $db)) { throw "Banco do PDV não encontrado em $db" }
if (Get-Process -Name 'Pdv' -ErrorAction SilentlyContinue) { throw 'Feche o PDV antes de trocar a senha.' }

$sqlite = (Get-Command sqlite3.exe -ErrorAction SilentlyContinue).Source
if (-not $sqlite) { throw 'sqlite3.exe não está no PATH — instale-o ou rode este script numa máquina que o tenha.' }

function Ler-Senha($rotulo) {
    $sec = Read-Host -AsSecureString $rotulo
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

$pin = (Ler-Senha 'Nova senha de administrador (4 a 6 digitos)').Trim()
if ($pin -notmatch '^\d{4,6}$') { throw 'A senha precisa ter de 4 a 6 digitos.' }
$conf = (Ler-Senha 'Repita a senha').Trim()
if ($pin -ne $conf) { throw 'As duas senhas nao conferem — nada foi alterado.' }

# Mesmo algoritmo do PDV: PBKDF2-SHA256, 100k iteracoes, sal 16, hash 32, base64.
$salt = [byte[]]::new(16)
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
$kdf  = New-Object Security.Cryptography.Rfc2898DeriveBytes(
            [Text.Encoding]::UTF8.GetBytes($pin), $salt, 100000,
            [Security.Cryptography.HashAlgorithmName]::SHA256)
try { $hash = $kdf.GetBytes(32) } finally { $kdf.Dispose() }

$h  = [Convert]::ToBase64String($hash)
$s  = [Convert]::ToBase64String($salt)
$em = (Get-Date).ToString('o')

Copy-Item $db ($db + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

# Aspas simples dobradas: hash base64 nunca traz aspas, mas a regra vale sempre.
$sql = @"
INSERT INTO operador (id,nome,pin_hash,pin_salt,perfil,ativo,atualizado)
VALUES ('_admin_','Administrador','$h','$s','gerente',0,'$em')
ON CONFLICT(id) DO UPDATE SET pin_hash='$h', pin_salt='$s', atualizado='$em';
INSERT INTO auditoria (evento,detalhe,criado_em)
VALUES ('senha_admin_trocada','trocada pelo script senha-admin.ps1','$em');
"@
$sql | & $sqlite $db

# Confere relendo: o hash gravado tem que ser o que acabamos de calcular.
$lido = (& $sqlite $db "SELECT pin_hash FROM operador WHERE id='_admin_';").Trim()
if ($lido -eq $h) {
    Write-Host 'OK — senha de administrador trocada.' -ForegroundColor Green
    Write-Host 'Ela vale para abrir a Configuracao do PDV (fora do modo de homologacao).'
} else {
    throw 'A conferencia falhou — restaure o backup .bak criado ao lado do pdv.db.'
}
