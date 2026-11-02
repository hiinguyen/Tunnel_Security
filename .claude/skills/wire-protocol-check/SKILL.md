---
name: wire-protocol-check
description: Invariants of the GSAN frame between node, gateway and PC. Load when editing WireProtocol/, NodeAgent/gsan_*.py or LineGateway/Gsan.
user-invocable: false
---

Two implementations of one protocol: C# (`WireProtocol/`, used by BackendV2, LineGateway and the simulators) and Python (`NodeAgent/gsan_protocol.py`, `gsan_client.py`).

## Frame

- Start `0x53`, Command, Length (2 bytes little-endian), NodeId, Payload, CRC16 Modbus (2 bytes little-endian), Stop `0x4D`.
- The CRC covers the bytes from Command through the end of Payload, excluding Start and Stop.
- Payload is at most 16 KiB. A frame with a bad CRC or Stop is dropped one byte at a time until the next Start.
- Commands: `0xA1` register, `0xA3` assign address, `0xA4` heartbeat (7 bytes), `0xA7` sensor data.
- Sensor flags: Radar `1`, Vxl `2`, Camera `4`.

## Rules when editing

- Any change to byte layout, constants or message types must update both sides in the same change.
- The shared source of truth is `WireProtocol/test-vectors/gsan.json`. Both `tests/WireProtocol.Tests` and `NodeAgent/tests` read it.
- To add or change a message, add the vector to `gsan.json` first, then make both test suites pass.
- Verify with `dotnet test tests/WireProtocol.Tests` and `cd NodeAgent && python3 -m pytest -q`.
- Call the `protocol-parity-reviewer` subagent for an independent comparison.
