FROM mcr.microsoft.com/powershell:lts-7.4-ubuntu-22.04

# PINNED: ExchangeOnlineManagement 3.7.2 — versions 3.9.x have a REST client
# bug where certificate-based app auth returns HTTP 401 on all cmdlet calls
# when running on updated PowerShell base images. See #64.
# TODO: Remove -RequiredVersion pin once Microsoft ships a fixed EXO module.
RUN pwsh -Command "Set-PSRepository PSGallery -InstallationPolicy Trusted; \
    Install-Module ExchangeOnlineManagement -RequiredVersion 3.7.2 -Force -Scope AllUsers; \
    Install-Module Az.Accounts, Az.KeyVault -Force -Scope AllUsers"

# Copy shared modules and entry point
COPY shared/modules/ /app/modules/
COPY shared/scripts/Invoke-Ingestion.ps1 /app/

# Copy container-specific files
COPY containers/exo-ingest/scripts/Connect-ToService.ps1 /app/
COPY containers/exo-ingest/scripts/entities/ /app/entities/

WORKDIR /app
ENTRYPOINT ["pwsh", "-File", "/app/Invoke-Ingestion.ps1"]
