# Recording backend

`ScreenRecorderLib.dll` is built from the commit recorded in `ScreenRecorderLib.manifest.json`, under the accompanying MIT license. The worker validates its SHA-256 before recording.

The small source patch:

- Stops static image overlays from continuously allocating and copying unchanged GPU textures in a busy loop.
- Exposes the actual Sink Writer video encoder hardware attribute and display name.
- Refuses an unverified or software encoder when hardware encoding is requested.
- Reuses at most eight encoder input textures through Media Foundation tracked-sample release callbacks. Slots are returned only after all external sample references are released; a full pool waits up to 250 ms and then fails explicitly.
- Shares the main protected D3D11 device and immediate context for static PNG overlays, with independent COM references. PNG readers upload once without shader/input-layout initialization; WGC, GIF and video sources retain their original devices. A dynamic replacement with a non-PNG reader falls back to an independent device.

The pool grows on demand and caps nominal BGRA pixel storage at 128 MiB, enough for eight 1920 by 1920 surfaces. Driver padding, WGC surfaces and internal encoder allocations are separate. Exposed pool counters report observed allocation, reuse and in-flight ownership; they are not total process or GPU memory.

The x64 DLL is distributed so normal plugin preparation needs the .NET SDK, not a C++ compiler. The backend also requires the x64 Visual C++ runtime and Windows Media Foundation. Rebuilding is optional and requires MSVC v143, C++/CLI, Windows SDK and .NET Framework 4.8 targeting tools:

```powershell
pwsh -NoProfile -File ./Rebuild-ScreenRecorderLib.ps1
```

The rebuild script fetches the pinned source into a separate directory, checks and applies the patch, registers the added helper files declared by the manifest for exact-diff verification, and updates only the distributed DLL and its manifest hash. Compiler versions and build timestamps may change the resulting hash; each shipped DLL is checked against its own manifest. Device sharing removes redundant device creation; its memory benefit must be measured with the same recording workload.
