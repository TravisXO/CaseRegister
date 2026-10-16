<#
.SYNOPSIS
  Generates the production secrets that are made locally: the audit chain's signing key, the data protection certificate and the proxy secret.

.DESCRIPTION
  Writes each secret to its own file in a folder you name, and prints only the file names, the setting each belongs in, and the certificate's
  expiry date. It never prints a secret. The folder must be outside this repository (and outside any git repository), so nothing can be committed
  by accident.

    audit-key-<id>.txt                          -> Audit__Keys__<id>               (base64, 32 random bytes)
    data-protection-certificate-base64.txt      -> DataProtection__CertificateBase64
    data-protection-certificate-password.txt    -> DataProtection__CertificatePassword
    proxy-secret.txt                            -> Proxy__Secret   (also set as PROXY_SECRET in Cloudflare Pages)
    data-protection-certificate.pfx             the certificate itself, for your backup
    NOTES.txt                                   what was made and when the certificate expires (no secrets)

  THEN: put every file in your password manager, set the values in Render and Cloudflare, and delete this folder.
  If the audit key is lost, the audit chain can never be verified again. If the certificate is lost, the stored data protection keys cannot be read:
  every session ends and every authenticator secret becomes unreadable. Back them up before you deploy.

  Production keys are made fresh here. Never reuse the development ones (those are in your user-secrets and have been on your machine).

.PARAMETER OutputDirectory
  Where to write the files. Must not exist yet or must be empty. Must be outside any git repository.

.PARAMETER AuditKeyId
  The id of the audit key (default k1). Use k2, k3 ... when you rotate: the id is part of the setting name and is stored with every audit entry.

.PARAMETER ProxySecretOnly
  Make only a new proxy secret, for a rotation (it goes in Proxy__NextSecret first; see docs/deployment.md).

.PARAMETER CertificateYears
  How long the data protection certificate is valid. Default 5.

.EXAMPLE
  .\scripts\New-ProductionKeys.ps1 -OutputDirectory $HOME\caseregister-keys
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidatePattern('^[A-Za-z0-9]{1,16}$')]
    [string]$AuditKeyId = 'k1',

    [switch]$ProxySecretOnly,

    [ValidateRange(1, 10)]
    [int]$CertificateYears = 5
)

$ErrorActionPreference = 'Stop'

function New-RandomBytes([int]$Count) {
    $bytes = New-Object byte[] $Count
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return $bytes
}

function Write-SecretFile([string]$Name, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $script:target $Name), $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$target = [System.IO.Path]::GetFullPath($OutputDirectory)

# Never inside a repository: a file made here must not be one "git add ." can pick up.
$probe = $target
while ($probe) {
    if (Test-Path (Join-Path $probe '.git')) {
        throw "That folder is inside a git repository. Choose one outside it, such as a folder in your home directory."
    }
    $probe = Split-Path $probe -Parent
}

if ((Test-Path $target) -and (Get-ChildItem $target -Force | Select-Object -First 1)) {
    throw "The folder already has files in it. Choose a new one, so nothing is overwritten."
}

New-Item -ItemType Directory -Path $target -Force | Out-Null

# Only you may read the folder (best effort: on a system without icacls this is skipped).
try {
    icacls $target /inheritance:r /grant:r ("{0}:(OI)(CI)F" -f $env:USERNAME) | Out-Null
} catch { }

$made = @()
$notes = @("Made $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm')) UTC. These files hold secrets: move them to your password manager, then delete this folder.", '')

# 32 random characters are not enough for a secret that guards the whole API; 48 random bytes, base64, is 64 characters.
Write-SecretFile 'proxy-secret.txt' ([Convert]::ToBase64String((New-RandomBytes 48)))
$made += [pscustomobject]@{ File = 'proxy-secret.txt'; Setting = 'Proxy__Secret (Render) and PROXY_SECRET (Cloudflare Pages). For a rotation: Proxy__NextSecret first.' }

if (-not $ProxySecretOnly) {
    Write-SecretFile "audit-key-$AuditKeyId.txt" ([Convert]::ToBase64String((New-RandomBytes 32)))
    $made += [pscustomobject]@{ File = "audit-key-$AuditKeyId.txt"; Setting = "Audit__Keys__$AuditKeyId (and Audit__CurrentKeyId=$AuditKeyId). LOSE THIS AND THE AUDIT CHAIN CAN NEVER BE VERIFIED." }

    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=CaseRegister Production Data Protection',
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears($CertificateYears))

    $password = [Convert]::ToBase64String((New-RandomBytes 32))
    $pfx = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)
    [System.IO.File]::WriteAllBytes((Join-Path $target 'data-protection-certificate.pfx'), $pfx)
    Write-SecretFile 'data-protection-certificate-base64.txt' ([Convert]::ToBase64String($pfx))
    Write-SecretFile 'data-protection-certificate-password.txt' $password

    $expiry = $certificate.NotAfter.ToString('yyyy-MM-dd')
    $made += [pscustomobject]@{ File = 'data-protection-certificate-base64.txt'; Setting = 'DataProtection__CertificateBase64' }
    $made += [pscustomobject]@{ File = 'data-protection-certificate-password.txt'; Setting = 'DataProtection__CertificatePassword' }
    $made += [pscustomobject]@{ File = 'data-protection-certificate.pfx'; Setting = '(the same certificate as a file, for your backup)' }
    $notes += "The data protection certificate expires on $expiry. Put a reminder in your calendar for two months before that date."
    $notes += 'Replacing it needs a small code change first (the application reads one certificate): see docs/deployment.md.'
}

[System.IO.File]::WriteAllText((Join-Path $target 'NOTES.txt'), ($notes -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Made in $target :"
$made | Format-Table -AutoSize -Wrap | Out-String | Write-Host
if (-not $ProxySecretOnly) {
    Write-Host "The data protection certificate expires on $expiry."
}
Write-Host 'Nothing secret was printed. Now: copy every file into your password manager, set the values in Render and Cloudflare, and delete the folder.'
Write-Host 'Back up the audit key and the certificate BEFORE the first deploy: neither can be made again.'
