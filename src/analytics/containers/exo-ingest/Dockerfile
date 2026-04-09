# Use the last known working base image from our ACR as a cached base.
# The lts-7.4-ubuntu-22.04 floating tag was updated after April 4, 2026
# and the updated image breaks ExchangeOnlineManagement's REST client
# (HTTP 401 on all cert-based app-only EXO cmdlet calls).
# TODO: Switch back to mcr.microsoft.com/powershell:lts-7.4-ubuntu-22.04
#       once the base image + EXO compatibility is resolved.
FROM matoolkitacr.azurecr.io/analytics/exo-ingest@sha256:b75af6502571b4398d441fb7d291b50ac14c75c9a5125278e27c8f8638dc85a2

# Overlay new code on top of the working base (preserves PS + EXO + Az modules)
COPY shared/modules/ /app/modules/
COPY shared/scripts/Invoke-Ingestion.ps1 /app/
COPY containers/exo-ingest/scripts/Connect-ToService.ps1 /app/
COPY containers/exo-ingest/scripts/entities/ /app/entities/

WORKDIR /app
ENTRYPOINT ["pwsh", "-File", "/app/Invoke-Ingestion.ps1"]
