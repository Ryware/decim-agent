# Decim Windows investigation agent

`decim-agent` is a minimal .NET 10 Windows console application that collects narrowly requested log evidence for an open Decim incident. It opens no inbound ports and communicates with the Decim API using outbound HTTP requests only. Non-loopback API URLs must use HTTPS.

> **Raw evidence warning:** directory listings, file bytes, file samples, and rendered Windows Event Log messages are intentionally sent unchanged and unredacted. Configure only sources whose contents Decim is permitted to receive.

## Supported tasks

- `directory.list` lists only the immediate files and directories below a configured source or relative subdirectory. Directories are traversed only when Decim sends another task.
- `file.read` reads one regular file or byte range below a configured root. Rooted paths, traversal, NTFS alternate data streams, and reparse-point or junction paths are rejected.
- `event-log.read` reads one configured Windows Event Log channel over a UTC half-open range and an allowed set of levels. Results are ordered oldest first and include record ID, UTC timestamp, level, provider, event ID, machine, and the unchanged rendered message.

A file result up to 5 MiB is uploaded as raw bytes in one request. For a larger effective range, the agent returns a JSON preview made from raw Base64-encoded 2 KiB samples. Sampling begins at a 2 MiB stride and becomes sparser when needed to keep the complete response at or below 5 MiB. An Event Log range or directory listing that would exceed 5 MiB fails without returning partial records.

The agent processes one leased task at a time, polls every five seconds by default, and retries transient network, `408`, `429`, and server failures with exponential backoff capped at one minute. Authentication and configuration failures terminate the process with a nonzero exit code. Ctrl+C stops it cleanly.

The agent does not execute commands, run database queries, parse logs locally, access unconfigured paths, accept inbound connections, or upload results in chunks. This release is a console application, not a Windows Service.

## Configure and run

The published archive contains `decim-agent.exe`, this README, the MIT license, and a secret-free `appsettings.template.json`. Copy the template to `appsettings.json` beside the executable and set:

| Setting | Meaning |
|---|---|
| `apiBaseUrl` | Decim API base URL. HTTPS is required except for HTTP loopback development. |
| `apiKey` | The one-time agent key returned by `POST /api/v1/tools/agent/register`. |
| `tenantId` | The matching tenant GUID. |
| `pollIntervalSeconds` | Optional polling interval from 1 through 300 seconds; defaults to 5. |
| `logDirectories` | Uniquely named, absolute Windows directory roots. |
| `eventLogs` | Uniquely named channels and allowed levels: `critical`, `error`, `warning`, `information`, or `verbose`. |

Start the agent from PowerShell or Command Prompt:

```powershell
.\decim-agent.exe
```

The Windows account running the process needs list/read access to each configured directory and file. It also needs read access to every configured Event Log channel; membership in the built-in **Event Log Readers** group is sufficient for many channels, while protected or application-specific channels may require an explicit channel ACL. Grant only the sources that should be exposed.

`appsettings.json` contains a live credential. Restrict its ACL to the operating account and administrators, for example from an elevated PowerShell prompt:

```powershell
icacls .\appsettings.json /inheritance:r /grant:r "DOMAIN\decim-agent-account:R" "Administrators:F"
```

Use the actual local or domain account name. Do not place `appsettings.json` in source control or in a directory writable by untrusted users. The agent never writes the API key or evidence content to its console output.

## Build, test, and package

Install the .NET 10 SDK, then run:

```powershell
dotnet restore .\Decim.Agent.slnx --locked-mode
dotnet build .\Decim.Agent.slnx --configuration Release --no-restore
dotnet format .\Decim.Agent.slnx --verify-no-changes --no-restore
dotnet test --solution .\Decim.Agent.slnx --configuration Release --no-build --no-restore --minimum-expected-tests 1
dotnet publish .\src\Decim.Agent\Decim.Agent.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output .\artifacts\publish
Compress-Archive -Path .\artifacts\publish\* -DestinationPath .\artifacts\decim-agent-win-x64.zip -Force
```

The self-contained publish is a single Windows x64 executable; the machine running it does not need a separate .NET runtime. The implementation uses standard .NET APIs plus `System.Diagnostics.EventLog` for native Windows Event Log access.

Every push to `master` runs the same locked restore, Release build, formatting, test, publish, and packaging sequence in GitHub Actions. A successful run creates a versioned GitHub Release and marks it latest. The current archive is always available at:

```text
https://github.com/Ryware/decim-agent/releases/latest/download/decim-agent-win-x64.zip
```

## API and retention behavior

Every agent request carries its issued key in `X-Api-Key` and its tenant in `X-Tenant-ID`. Agent credentials cannot authenticate Decim control-plane endpoints, and the Decim bootstrap key cannot authenticate agent routes. Polling advertises only source names, Event Log channels and allowed levels, plus hostname, OS version, agent version, and heartbeat time; configured filesystem paths and the API key are not advertised.

Decim leases a task for ten minutes. An expired lease can be delivered again, so successful result submission is idempotent for network retries. Explicit task failures are retained and are not retried automatically. Closing an incident cancels outstanding work and rejects later task results. Three days after closure, Decim deletes the stored evidence payload while retaining task timestamps, result type, byte length, and SHA-256 metadata.

Licensed under the [MIT License](LICENSE).
