function Get-TenantRegistry {
    param(
        [Parameter(Mandatory)][string]$VaultName
    )

    $registryJson = Get-AzKeyVaultSecret -VaultName $VaultName -Name 'tenant-registry' -AsPlainText
    return ($registryJson | ConvertFrom-Json)
}

function Get-CertificateFromKeyVault {
    param(
        [Parameter(Mandatory)][string]$VaultName,
        [Parameter(Mandatory)][string]$CertName
    )

    $cert = Get-AzKeyVaultCertificate -VaultName $VaultName -Name $CertName
    $secret = Get-AzKeyVaultSecret -VaultName $VaultName -Name $cert.Name -AsPlainText
    $certBytes = [System.Convert]::FromBase64String($secret)

    $pfxPath = Join-Path $env:TEMP "$CertName.pfx"
    [System.IO.File]::WriteAllBytes($pfxPath, $certBytes)

    return $pfxPath
}

function Get-CertificateBytes {
    param(
        [Parameter(Mandatory)][string]$VaultName,
        [Parameter(Mandatory)][string]$CertName
    )

    $cert = Get-AzKeyVaultCertificate -VaultName $VaultName -Name $CertName
    $secret = Get-AzKeyVaultSecret -VaultName $VaultName -Name $cert.Name -AsPlainText
    return [System.Convert]::FromBase64String($secret)
}

function Remove-CertificateFile {
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (Test-Path $Path) {
        $bytes = [byte[]]::new((Get-Item $Path).Length)
        [System.IO.File]::WriteAllBytes($Path, $bytes)
        Remove-Item $Path -Force
    }
}

Export-ModuleMember -Function Get-TenantRegistry, Get-CertificateFromKeyVault, Get-CertificateBytes, Remove-CertificateFile
