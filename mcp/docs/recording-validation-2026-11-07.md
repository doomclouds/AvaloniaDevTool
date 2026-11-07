# Recording 0.1.3 validation

- Backend: WGC, D3D11, verified hardware H.264 through Media Foundation, fragmented MP4. No FFmpeg runtime dependency.
- Worker cleanup drains timer callbacks before native recorder disposal; Git preserves the exact source-patch bytes for checksum verification after installation.
- Five MCP tools cover start, status, stop, list and caller-local SHA-256-verified export. The output directory is optional.
- Existing AutomationSample desktop tests cover whole-window and control ROI, Agent click/wheel markers, window movement, streaming file growth, export and cancelled-stop retry: 4 passed.
- Final release checks: Protocol 48, Agent recording/input 22, MCP recording/export 9 passed. Debug gateway/HTTP/recording regression: 28 passed.
- Known unrelated failure: the Release gateway active-wait test timed out; reproduced with tests, Agent and CLI all from baseline `ad5b5d4`. Do not interpret the targeted checks as a clean full Release suite.

## Resource measurements

Intel Arc 140T, 16 logical processors; AutomationSample output 1920x1522, 30 fps. Two short recorded ABBA blocks with Agent input:

| Measurement | Result |
| --- | --- |
| Sampled worker peak working set | 341.2 / 340.8 MiB, previously 633.8 / 635.1 MiB |
| Worker CPU | 0.248 / 0.262 logical cores |
| Managed heap | About 4.76 MiB; zero Gen0/1/2 collections during the sample |
| Encoder texture pool | At most 8 textures / 128 MiB nominal BGRA bytes; zero timeouts and zero in-use textures after stop |

These are single-machine short-run results, not a 100 MiB guarantee. Pool bytes exclude other GPU and driver allocations; MCP roundtrip timings do not measure input-to-photon latency.

FFmpeg pure QSV capture used about 225 MiB and 0.042 core. The functional pointer/click-overlay prototype required CPU readback/composition/upload and used 378.4 MiB and 0.351 core, excluding its separate controller. The existing backend is retained.

Local evidence remains under ignored `mcp/artifacts/recording-memory-shared-final/`, `recording-release-check/` and `recording-validation-summary.json`; videos and build outputs are not distributed with the plugin.
