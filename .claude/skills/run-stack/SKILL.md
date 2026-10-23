---
name: run-stack
description: Bring up the dev stack on Linux (docker, BackendV2, node simulators) for E2E. Use when you need to run, test or screenshot the real system.
disable-model-invocation: true
---

This machine has no system dotnet. The .NET 10 SDK, .NET 8 runtime and dotnet-ef live in `~/.cache/tunnel-security-dotnet/`.
Run every step from the repo root.

## 1. Infrastructure

- `docker compose --profile sqlserver up -d` starts Mosquitto (Dynamic Security), TimescaleDB (port 5433) and SQL Server (`sa` / `Dev-Passw0rd!`).
- `docker compose ps` must show timescaledb `healthy` before you start the backend.

## 2. BackendV2

- `~/.cache/tunnel-security-dotnet/run-backend.sh > ~/.cache/tunnel-security-dotnet/logs/backend.log 2>&1 &` serves `http://localhost:5080`.
- Wait until `curl -s http://localhost:5080/` answers (or the log shows `Now listening`).
- Log in to the API with `admin` / `ChangeMe123!`.

## 3. Node simulators

- `~/.cache/tunnel-security-dotnet/run-sim.sh` runs NodePublisherSim, which sends binary GSAN frames to topic `sensors/{NODE_ID}/reading`.
- Options: `MQTT_BROKER_HOST`, `MQTT_BROKER_PORT`, `NODE_ID`, `PUBLISH_INTERVAL_SECONDS`.
- Camera simulator: `dotnet run --project CameraPublisherSim` (load env with `. ~/.cache/tunnel-security-dotnet/env.sh`).
- Fake NodeAgent: `cd NodeAgent && python3 node_agent.py --config node_config.example.json --fake`.

## 4. Stop

- Find the PID with `pgrep -f "bin/Debug/net10.0/BackendV2$"`, then `kill <pid>`.
- Do not use `pkill -f` with a broad pattern, because it also matches the calling shell.
- `docker compose down` stops the infrastructure (add `-v` only to delete data).

Station (WinUI 3) builds only on Windows, so skip it on this machine.
