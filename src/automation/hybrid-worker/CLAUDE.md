# Claude Context — Hybrid Worker

## What This Project Is

An on-premises PowerShell worker running as a native Windows Service, part of the Migration Automation Toolkit's **automation subsystem**. It executes migration functions against both cloud (Entra ID, Exchange Online) and on-premises (Active Directory, Exchange Server, SharePoint Online, Teams) services using a dual-engine architecture: PS 7.x RunspacePool for cloud functions and PS 5.1 PSSession pool for on-prem functions.

## Project Structure

```
hybrid-worker/
├── CLAUDE.md                              ← this file
├── version.txt                            # Current version
├── Install-HybridWorker.ps1               # First-time setup (run as admin)
├── Uninstall-HybridWorker.ps1             # Clean removal
├── Download-Dependencies.ps1              # Fetch NuGet packages for dotnet-libs/
├── service-host/                          # .NET 8 Worker Service (Windows Service host)
│   ├── HybridWorker.ServiceHost.csproj
│   ├── Program.cs
│   ├── WorkerProcessService.cs
│   └── appsettings.json
├── src/
│   ├── worker.ps1                         # Main entry point (12-phase boot)
│   ├── config.ps1                         # JSON config loader + env var overrides
│   ├── logging.ps1                        # App Insights telemetry (adapted from cloud-worker)
│   ├── auth.ps1                           # SP cert auth + on-prem credential retrieval
│   ├── service-bus.ps1                    # SB integration (ClientCertificateCredential)
│   ├── runspace-manager.ps1               # PS 7.x RunspacePool (reused from cloud-worker)
│   ├── session-pool.ps1                   # PS 5.1 PSSession pool (NEW)
│   ├── job-dispatcher.ps1                 # Dual-engine routing + update check
│   ├── service-connections.ps1            # Service registry + function-to-engine mapping
│   ├── update-manager.ps1                 # Blob storage version check + zip download
│   └── health-check.ps1                   # Health endpoint (adapted from cloud-worker)
├── modules/
│   ├── StandardFunctions/                 # Copy of cloud-worker StandardFunctions
│   ├── HybridFunctions/                   # On-prem functions (PS 5.1 via PSSession)
│   └── CustomFunctions/                   # Customer-specific modules
├── config/
│   └── worker-config.example.json         # Example configuration file
├── tests/
│   └── Test-WorkerLocal.ps1               # Parse + structure validation tests
└── dotnet-libs/                           # .NET assemblies (fetched by Download-Dependencies.ps1)
```

## Key Architecture Decisions

- **Dual-engine**: RunspacePool (PS 7.x) for cloud modules, PSSession pool (PS 5.1) for on-prem modules
- **Service Bus SDK**: Same Azure.Messaging.ServiceBus .NET assembly as cloud-worker, but authenticated via `ClientCertificateCredential` instead of `ManagedIdentityCredential`
- **Windows Service host**: .NET 8 Worker Service manages the `pwsh.exe` process lifecycle (start, stop, restart on crash, graceful shutdown via process signal)
- **Self-update**: Polls Azure Blob Storage for new versions, downloads + stages, worker exits and service host restarts it, new version applied at boot
- **Certificate auth**: SP certificate in `Cert:\LocalMachine\My` for Azure resources; KV-stored PFX for target tenant MgGraph/EXO (same as cloud-worker)
- **On-prem credentials**: Username/password stored as JSON secrets in Key Vault, retrieved at startup
- **Configurable services**: Each service connection (entra, exchangeOnline, activeDirectory, exchangeServer, sharepointOnline, teams) can be independently enabled/disabled

## Service Bus Message Format

Same as cloud-worker — see `src/automation/cloud-worker/CLAUDE.md` for job/result message schemas.

## Tests

Run `pwsh -File tests/Test-WorkerLocal.ps1` — validates parse correctness for all .ps1 files, module structure, manifest exports, and project structure.
