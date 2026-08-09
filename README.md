# agent — local investigation agent

**Status: not implemented.** This directory is a placeholder describing intended scope.

A lightweight agent deployed inside the customer's infrastructure. It is the platform's
only point of contact with customer systems.

## Deployment targets

Docker container · Kubernetes workload · Windows Service · Linux systemd unit.

## What it may collect

- Application / ETL log files
- Windows Event Log, journald, Docker logs, Kubernetes logs
- ETL execution history
- Database metadata (schemas, row counts, constraints)
- Results of **approved read-only** SQL queries
- Mapping files and configuration files
- Deployment / version information

## Behavioural rules

1. **Outbound HTTPS only.** No inbound ports, no listening sockets.
2. **Do not stream everything.** Collect evidence scoped to a specific incident and a
   specific time window, on instruction from the platform.
3. **Redact before sending.** Connection strings, tokens, keys, and PII are scrubbed
   locally — the platform must never be the first line of defence.
4. **Normalize.** Send structured evidence records, not raw blobs, so the investigation
   engine reasons over a consistent shape across SQL Server, PostgreSQL, and file logs.
5. **Heartbeat.** Report version, host, OS, configured log sources, connected databases,
   granted permissions, and recent errors — surfaced on the dashboard's Agents screen.

The evidence record shape the platform expects is drafted in
[`../admin/src/entities/incident/model/types.ts`](../admin/src/entities/incident/model/types.ts).
