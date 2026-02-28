function Write-Log {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR', 'DEBUG')][string]$Level = 'INFO',
        [string]$Entity = '',
        [string]$TenantKey = ''
    )

    $timestamp = Get-Date -Format 'yyyy-MM-ddTHH:mm:ss.fffZ'
    $context = @()
    if ($TenantKey) { $context += "tenant=$TenantKey" }
    if ($Entity) { $context += "entity=$Entity" }
    $contextStr = if ($context.Count -gt 0) { " [$($context -join ', ')]" } else { '' }

    $line = "[$timestamp] [$Level]$contextStr $Message"

    switch ($Level) {
        'ERROR' { Write-Error $line }
        'WARN'  { Write-Warning $line }
        'DEBUG' { Write-Verbose $line }
        default { Write-Host $line }
    }
}

Export-ModuleMember -Function Write-Log
