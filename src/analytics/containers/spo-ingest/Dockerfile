FROM mcr.microsoft.com/powershell:lts-ubuntu-22.04

RUN pwsh -Command "Set-PSRepository PSGallery -InstallationPolicy Trusted; \
    Install-Module PnP.PowerShell -Force -Scope AllUsers; \
    Install-Module Az.Accounts, Az.KeyVault, Az.Storage -Force -Scope AllUsers"

# Copy shared modules and entry point
COPY shared/modules/ /app/modules/
COPY shared/scripts/Invoke-Ingestion.ps1 /app/

# Copy container-specific files
COPY containers/spo-ingest/scripts/Connect-ToService.ps1 /app/
COPY containers/spo-ingest/scripts/entities/ /app/entities/

WORKDIR /app
ENTRYPOINT ["pwsh", "-File", "/app/Invoke-Ingestion.ps1"]
