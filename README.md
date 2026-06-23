# Dc — Universal OPC Data Collection Client

**English** · [简体中文](README.zh-CN.md)

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![GUI: Windows](https://img.shields.io/badge/GUI-Windows-0078D6.svg)](#)
[![Headless: Linux · Docker](https://img.shields.io/badge/headless-Linux%20%C2%B7%20Docker-1D63ED.svg)](#)

A .NET 8 OPC data-collection client: browse the OPC address space, configure collection tasks, subscribe to **OPC UA / DA / AE** data in real time, and publish over TCP to a downstream broker.

**Core principles**: generic, decoupled, extensible — no assumptions about the broker protocol, no lock-in to a specific OPC SDK.

One collection engine, two forms: a **Windows desktop app** (`Dc.App`, WPF GUI, all protocols UA/DA/AE) and a **headless server** (`Dc.Cli`, pure .NET, **deployable on Linux/Docker**, UA collection + publish).

---

## Download

See the latest **[Releases](https://github.com/DevMinions/wpf-opc-client/releases/latest)**:

| Form | Artifact | Usage | Protocols |
|---|---|---|---|
| Windows desktop GUI | `Dc-v<ver>-win-x64.zip` | unzip, run `Dc.App.exe` | UA / DA / AE |
| Linux headless collector | `Dc.Cli-v<ver>-linux-x64.tar.gz` (also `-arm64`) | unzip, run `./Dc.Cli` | UA only |
| Docker image | `ghcr.io/devminions/dc-cli:<ver>` (multi-arch amd64/arm64) | see below | UA only |

All are self-contained — **the target machine needs no preinstalled .NET runtime**. The headless collector reads configured tasks from `sqlite.db` (which can be configured in the GUI and shared; the schema is identical).

```bash
# Run the headless collector in Docker: put a sqlite.db with tasks in host ./data
docker run -d --name dc-collector -v "$PWD/data:/data" ghcr.io/devminions/dc-cli:latest
docker logs -f dc-collector
```

> The Windows zip/installer is not code-signed; on first run SmartScreen warns "unknown publisher" → click "More info → Run anyway".

---

## Screenshots

> Fluent Design · light/dark theme follows the system · live screenshots from the Windows app.

![Dashboard](docs/screenshots/dashboard.png)

| Collection tasks (master-detail + 5 tabs) | Browse nodes |
|---|---|
| ![Tasks](docs/screenshots/workspace.png) | ![Browse](docs/screenshots/browse.png) |
| **Live data** | **Diagnostics** |
| ![Live data](docs/screenshots/livedata.png) | ![Diagnostics](docs/screenshots/diagnostics.png) |
| **Logs** | **Settings** |
| ![Logs](docs/screenshots/logs.png) | ![Settings](docs/screenshots/settings.png) |

---

## Features

| Module | Notes |
|---|---|
| Config management | Tasks / Tags / Formulas / system config, EF Core + SQLite. Tags attach **directly to a task** (no group layer) |
| OPC UA subscribe + browse + read | OPC Foundation .NET Standard SDK, certificate trust chain, **KeepAlive auto-reconnect on disconnect** |
| OPC DA subscribe + browse + IP scan | Technosoftware DaAeHdaClient (COM/DCOM, Windows-only) |
| OPC AE subscribe + Area/Source browse | same as above |
| Live data view | event-driven + tri-state quality coloring (Good/Uncertain/Bad) |
| Task orchestration | `TaskOrchestrator`: start/stop · hot add/remove tags · heartbeat monitoring · auto-restart on timeout |
| TCP publishing | MessagePack / JSON switchable, wire v1.1 (magic + format-id), cooldown reconnect + optional offline queue |
| Diagnostics + observability | WPF panel (per-task rate/error/restart/heartbeat sparkline); also exposed via `System.Diagnostics.Metrics` + `/metrics` (rate/errors/restarts/heartbeat-age/queue-backlog/dropped-frames — scrapeable by dotnet-counters / OpenTelemetry / Prometheus) + periodic structured diagnostic logs (incl. queue-overflow drop edge alerts) |
| **Headless / service mode** | `Dc.Cli` console: loads tasks from the DB, runs UA collection + publishing, **deployable on Linux/Docker**, reuses the same engine |
| Excel import/export of Tags | ClosedXML (Item + DataType columns; import lands tags in the current task) |
| Discover → configure flow | Browse the address space, multi-select nodes, and **"Add as Tags"** in bulk to a task (data type auto-mapped); then jump straight to the task |
| System tray + single-instance lock · rolling logs (Serilog) | |

See the roadmap in [`ROADMAP.md`](./ROADMAP.md).

---

## Architecture

Clean Architecture with one-way dependencies (UI → Infrastructure → Abstractions/Domain):

```
src/
├── Dc.Domain/             # Entities (no external dependencies)
├── Dc.Opc.Abstractions/   # IOpcSubscriber / IOpcBrowser / TagValue / OpcProtocol
├── Dc.Opc.Ua/             # UA subscriber + browser (OPC Foundation)
├── Dc.Opc.Da/             # DA subscriber + browser (Technosoftware)
├── Dc.Opc.Ae/             # AE subscriber + browser (Technosoftware)
├── Dc.Infrastructure/     # EF Core + serialization + TCP publishing + orchestration + observability
├── Dc.App/                # WPF app (MVVM + DI, Windows)
└── Dc.Cli/                # Headless collector (console, Linux/Docker, UA only)
tests/                     # xUnit unit + integration tests (incl. in-process UA server end-to-end)
tools/Dc.WireDump/         # Receiver-side debug tool (parses wire frames)
```

### Key design choices

- **Use DbContext directly, no Repository wrapper** — EF Core is already a Repository + Unit of Work.
- **Serializer separated from Publisher + generic** — `PublishAsync<T>(T)` is message-type agnostic; the broker-side protocol is the deployer's choice.
- **`TaskOrchestrator` single-object API** — one object owns the whole task lifecycle + hot updates + watchdog.
- **`IOpcSubscriber` exposes only `ChannelReader<T>`** — outside code pulls data read-only; subscriber state is not externally writable.
- **Quality codes parsed with `0xC0/0x40/0x00` bit masking** (not `quality > 0`).
- **DB columns use the `dc_` prefix + snake_case** (`EFCore.NamingConventions`).

---

## Build

Requires the .NET 8 SDK. The Technosoftware DA/AE/HDA client sources that OPC DA/AE depend on are vendored under
[`vendor/ClassicClient/`](vendor/ClassicClient) (GPL-3.0) — no extra fetch needed:

```bash
git clone https://github.com/DevMinions/wpf-opc-client.git
cd wpf-opc-client
# Build per project (global Platform=x64 propagates to all P2P refs, incl. vendor COM)
dotnet build src/Dc.App/Dc.App.csproj -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.Infrastructure.Tests -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

On Windows you can use the script:

```powershell
.\build.ps1                 # build
.\build.ps1 -Target test    # run tests
.\build.ps1 -Target run     # launch the app
```

**Why the two `-p` parameters**:
- `Platform=x64` — the vendor `DaAeHdaClient.Com`'s `NETCORE` macro is only defined on x86/x64; otherwise compilation fails with `FILETIME` not found.
- `CustomTestTarget=net8.0-windows` — makes vendor build a single net8 TFM (default builds 5 TFMs incl. net9/net10), which would hit NETSDK1045 without the matching SDK.

> Linux/WSL/macOS can **build** the non-GUI projects and run cross-platform tests, but the **WPF app and OPC DA/AE (COM) only run on Windows**.

---

## Running

### Desktop GUI (Windows, all protocols)

```powershell
dotnet run --project src/Dc.App
```

On first run it creates, in the working directory: `sqlite.db` (empty), `logs/dc-yyyyMMdd.log`, and `pki/` (OPC UA self-signed certificates).

### Collecting OPC UA

1. **Collection tasks → New**: pick protocol **UA**, set the server to `opc.tcp://host:4840`, and set the TCP address to the downstream broker's `host:port`.
2. **Browse nodes** → enter the server URI → **Connect** → tick multiple nodes in the tree → **"Add as Tags"** to the target task (or **+ New task**). *(Or: in the task's Tags tab, click **New** and enter the Item by hand.)*
3. Select the task → **Start** → watch values stream in under **Live data**.

Tags attach directly to a task — there is no group layer to set up.

### Collecting OPC DA / AE

You need an OPC DA/AE server as the data source (pick one):
- **KEPServerEX Demo** (built-in Simulator channel, ProgID `Kepware.KEPServerEX.V6`, 2-hour restart limit)
- **Matrikon OPC Simulation Server** (free)
- **Technosoftware Demo Server** (shipped with their DA/AE/HDA client suite; not vendored here to keep the repo small)

Flow: Collection tasks → protocol **DA**, set node to host/IP (`localhost` for the local machine), set ProgID to the server's ProgID. On the Browse page, choosing DA shows a "Scan OPC servers" bar that auto-fills what it finds.

> Cross-machine DA depends on OPCEnum + DCOM permissions and is fiddly to configure; get the flow working with a local demo server first.

### Headless (Linux / Docker, UA only)

`Dc.Cli` has no WPF dependency; it loads configured tasks from the same `sqlite.db` and runs UA collection + TCP publishing — ideal for a server/edge daemon. DA/AE go through COM and require Windows, so they are not in the headless build.

```bash
# tarball (bundled runtime, no .NET install needed)
tar xzf Dc.Cli-v<ver>-linux-x64.tar.gz && ./Dc.Cli

# or Docker (put a sqlite.db with tasks in the host ./data volume; Database__Path already points at /data; 9090 is the diagnostics port)
docker run -d --name dc-collector -v "$PWD/data:/data" -p 9090:9090 ghcr.io/devminions/dc-cli:latest
```

The task DB `sqlite.db` can be configured in the Windows GUI and copied/shared to the headless side (identical schema). Logs go to stdout (`docker logs`), and `Ctrl+C` / `docker stop` shuts down gracefully. From source: `dotnet run --project src/Dc.Cli`.

**Diagnostics endpoints** (listens on `:9090` by default, configurable/disableable via `Diagnostics:Http`):

| Path | Purpose |
|---|---|
| `GET /healthz`, `/readyz` | liveness/readiness probes (usable directly by Docker `HEALTHCHECK` and k8s) |
| `GET /metrics` | Prometheus text, exports `dc_collector_*` (running tasks, per-task values/publish-errors/restarts/subscribed-tags/heartbeat-age/queue-backlog-bytes/cumulative-dropped-frames) |

The image has a built-in `HEALTHCHECK` (calls `Dc.Cli --healthcheck` to probe `/healthz`, so no curl needed in the image).

> ⚠️ `/metrics` has **no auth** and binds all interfaces by default (`http://+:9090/`); expose it only to a trusted scraping network (Prometheus / k8s) and **don't map 9090 to the public internet**. Disable via `Diagnostics:Http:Enabled=false` if unused.
> The image runs as non-root (uid 10001): when bind-mounting a host dir with `-v`, that dir must be writable by uid 10001 (`chown -R 10001 ./data`, or adjust with `docker run --user`).

### Configuration

`appsettings.json` in the same directory can be externalized (no recompile):

```json
{
  "Database": { "Path": "sqlite.db" },
  "Messaging": { "Format": "msgpack" },
  "Orchestrator": { "WatchdogIntervalSeconds": 30, "HeartbeatTimeoutSeconds": 120 },
  "Diagnostics": {
    "ReportIntervalSeconds": 30, "EnableLogging": true, "EnableMetrics": true,
    "Http": { "Enabled": true, "Prefix": "http://+:9090/" }
  },
  "OpcUa": { "AutoAcceptUntrustedCertificates": false, "MinimumCertificateKeySize": 2048 }
}
```

---

## Building an installer

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php) (default path, or set env var `INNO_SETUP_DIR`).

```powershell
.\build.ps1 -Target installer                 # produces build/installer/Dc-Setup-x64-<version>.exe
.\build.ps1 -Target installer -Version 1.2.3
```

Produces a self-contained installer (the user's machine needs no preinstalled .NET runtime) with the published output + scripts + docs + LICENSE. The installer detects OPCEnum and prompts to install the OPC Foundation Core Components if missing.

> Not included: code signing (an EV cert is recommended for production to avoid SmartScreen), MSI format, auto-update.

### Release artifacts (CI-automated)

Pushing a `v*` tag triggers [`release.yml`](.github/workflows/release.yml), which builds and attaches everything to one GitHub Release:

- **Windows** — `Dc.App` self-contained zip
- **Linux** — `Dc.Cli` self-contained single-file tarball (x64 / arm64)
- **Docker** — multi-arch image (amd64 / arm64) pushed to GHCR `ghcr.io/<owner>/dc-cli` (`<ver>` + `latest`)

```bash
git tag v1.2.3 && git push origin v1.2.3
```

> The GHCR image is **private** on first push; set the `dc-cli` package to public in GitHub Packages settings to allow anonymous `docker pull`.

---

## OPC UA certificate management

Default security baseline: `AutoAcceptUntrustedCertificates = false`, `MinimumCertificateKeySize = 2048`.

The first connection to an unknown UA server is rejected and its certificate written to `pki/rejected/certs/`. After manual review, move it to `pki/trusted/certs/` and restart to connect.

| Directory | Contents |
|---|---|
| `pki/own/` | the client's own certificate (auto-generated) |
| `pki/trusted/` | trusted server certificates (placed in manually) |
| `pki/issuer/` | trusted CA issuer certificates |
| `pki/rejected/` | rejected certificates (for review) |

For dev you can set `"AutoAcceptUntrustedCertificates": true` to skip this (not recommended for production).

---

## Known limitations

- **Remote DA discovery/connection is constrained by DCOM config** — cross-machine DA hard-depends on OPCEnum + DCOM permissions.
- **Single-connection TCP publisher** — with a 2s reconnect cooldown + send timeout; the offline queue is optional (off by default).
- **MessagePack carries no type info** — the receiver must know the message type; see [`docs/wire-format.md`](docs/wire-format.md).
- **Installer is not code-signed** — SmartScreen will block the first run in strict environments.

---

## Contributing

Issues and PRs welcome. Before submitting, make sure `dotnet test tests/Dc.Infrastructure.Tests` passes. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

**GPL-3.0** (see [LICENSE](LICENSE)).

This project's dependencies `OPCFoundation.NetStandard.Opc.Ua.Client` and `Technosoftware.DaAeHdaClient` are dual-licensed (GPL / commercial); non-purchasers default to GPL, so this project is distributed as a whole under GPL-3.0 — **anyone may freely use, modify, and redistribute it, but derivative works must likewise be open-sourced (GPL)**. For closed-source commercial use, you must separately purchase commercial licenses from OPC Foundation and Technosoftware.

The full third-party license list is in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
