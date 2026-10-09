<#
.SYNOPSIS
  Creates a self-signed certificate for local development and stores it in user-secrets.

.DESCRIPTION
  The data protection keys (which encrypt the sign-in cookie and tokens) are stored in the database
  and encrypted with a certificate. This creates a throwaway one for Development and puts it, as a
  base64 PFX plus a random password, into the CaseRegister.Api user-secrets. Nothing is written to
  disk or printed, so there is nothing to commit by accident.

  Do not reuse this certificate anywhere else. In Production the certificate and its password come
  from the DataProtection__CertificateBase64 and DataProtection__CertificatePassword environment
  variables.

  Re-running it replaces the certificate. Keys already stored in the database were encrypted with the
  old one and become unreadable, which only matters for a development database: sessions end.
#>
param(
    [string]$Project = (Join-Path $PSScriptRoot '..\CaseRegister.Api\CaseRegister.Api.csproj')
)

$ErrorActionPreference = 'Stop'

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=CaseRegister Development Data Protection',
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(5))

$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)

$pfx = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)

dotnet user-secrets set 'DataProtection:CertificateBase64' ([Convert]::ToBase64String($pfx)) --project $Project | Out-Null
dotnet user-secrets set 'DataProtection:CertificatePassword' $password --project $Project | Out-Null

Write-Host "Stored a new development data protection certificate in user-secrets (expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))."
