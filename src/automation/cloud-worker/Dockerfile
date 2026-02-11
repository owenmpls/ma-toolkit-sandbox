FROM mcr.microsoft.com/powershell:7.4-ubuntu-22.04

SHELL ["pwsh", "-Command"]

# Install required PowerShell modules
RUN Set-PSRepository -Name PSGallery -InstallationPolicy Trusted && \
    Install-Module -Name Az.Accounts -Scope AllUsers -Force && \
    Install-Module -Name Az.KeyVault -Scope AllUsers -Force && \
    Install-Module -Name Microsoft.Graph.Authentication -Scope AllUsers -Force && \
    Install-Module -Name Microsoft.Graph.Users -Scope AllUsers -Force && \
    Install-Module -Name Microsoft.Graph.Groups -Scope AllUsers -Force && \
    Install-Module -Name Microsoft.Graph.Identity.SignIns -Scope AllUsers -Force && \
    Install-Module -Name ExchangeOnlineManagement -Scope AllUsers -Force

# Install .NET packages for Service Bus, App Insights, and Azure Identity
SHELL ["/bin/bash", "-c"]
RUN apt-get update && apt-get install -y wget apt-transport-https curl && \
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb && \
    apt-get update && apt-get install -y dotnet-runtime-8.0 && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

SHELL ["pwsh", "-Command"]

# Download and cache .NET assemblies for Service Bus, App Insights, and Azure Identity
RUN $nugetBase = 'https://www.nuget.org/api/v2/package'; \
    $libPath = '/opt/dotnet-libs'; \
    New-Item -ItemType Directory -Path $libPath -Force | Out-Null; \
    @( \
        @{ Name = 'Azure.Messaging.ServiceBus'; Version = '7.18.1' }, \
        @{ Name = 'Microsoft.Azure.Amqp'; Version = '2.6.9' }, \
        @{ Name = 'Azure.Identity'; Version = '1.13.1' }, \
        @{ Name = 'Azure.Core'; Version = '1.44.1' }, \
        @{ Name = 'Microsoft.ApplicationInsights'; Version = '2.22.0' }, \
        @{ Name = 'Microsoft.Bcl.AsyncInterfaces'; Version = '8.0.0' }, \
        @{ Name = 'System.Memory.Data'; Version = '1.0.2' }, \
        @{ Name = 'System.ClientModel'; Version = '1.1.0' }, \
        @{ Name = 'Microsoft.Identity.Client'; Version = '4.65.0' }, \
        @{ Name = 'Microsoft.Identity.Client.Extensions.Msal'; Version = '4.65.0' }, \
        @{ Name = 'System.Diagnostics.DiagnosticSource'; Version = '8.0.1' } \
    ) | ForEach-Object { \
        $pkg = $_; \
        $zipPath = "/tmp/$($pkg.Name).zip"; \
        $extractPath = "/tmp/$($pkg.Name)"; \
        Invoke-WebRequest -Uri "$nugetBase/$($pkg.Name)/$($pkg.Version)" -OutFile $zipPath; \
        Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force; \
        $dllSource = Join-Path $extractPath "lib/net8.0/$($pkg.Name).dll"; \
        if (-not (Test-Path $dllSource)) { \
            $dllSource = Join-Path $extractPath "lib/netstandard2.0/$($pkg.Name).dll"; \
        }; \
        if (-not (Test-Path $dllSource)) { \
            $dllSource = Get-ChildItem -Path $extractPath -Recurse -Filter "$($pkg.Name).dll" | \
                Where-Object { $_.FullName -match 'net8|net6|netstandard2' } | \
                Select-Object -First 1 -ExpandProperty FullName; \
        }; \
        if ($dllSource) { Copy-Item $dllSource -Destination $libPath -Force }; \
        Remove-Item $zipPath -Force; \
        Remove-Item $extractPath -Recurse -Force; \
    }

WORKDIR /app

# Copy application code
COPY src/ ./src/
COPY modules/ ./modules/

# Set environment variable for .NET library path
ENV DOTNET_LIB_PATH=/opt/dotnet-libs

SHELL ["/bin/bash", "-c"]
RUN useradd --create-home --shell /bin/bash worker
USER worker

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["pwsh", "-File", "/app/src/worker.ps1"]
