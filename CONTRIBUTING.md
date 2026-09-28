# Contributing to FFmpeg.Interop

Bug reports, reproductions, and small focused pull requests are welcome. Please open an issue before
starting anything large, so we can agree on the shape before you spend time on it.

## Getting set up

You need the .NET SDK pinned in [`global.json`](global.json) and PowerShell 7.

```bash
dotnet tool restore --disable-parallel
./eng/fetch-ffmpeg.ps1
dotnet build FFmpeg.Interop.slnx
dotnet test --solution FFmpeg.Interop.slnx --filter "TestCategory!=RequiresGpu"
```

`fetch-ffmpeg.ps1` puts a pinned FFmpeg 9 build (verified by SHA-256) into `native/<rid>`: BtbN's LGPL
builds on Windows and Linux, Homebrew's on macOS. The tests load it and use its `ffmpeg` and `ffprobe`
tools as the reference.

## Tests

Most tests run against real FFmpeg and check the bindings against FFmpeg's own tools: decoded frames
must match `ffmpeg -f framemd5` bit for bit, encoded streams must read back in `ffprobe` and measure
well against the source with the `psnr` filter.

- `TestCategory("Integration")` needs the fetched FFmpeg build. CI runs it on every platform.
- `TestCategory("RequiresVulkan")` needs a Vulkan driver; CI installs Mesa's software one on Linux.
- `TestCategory("RequiresGpu")` needs a real GPU and never runs in CI. The vendor suites inside it
  are `RequiresNvidia` and `RequiresAmf` (Windows), `RequiresVaapi` (Linux) and
  `RequiresVideoToolbox` (macOS). Run `./eng/test-machine.ps1`, which picks the suites for the GPUs
  present. A suite that is picked fails when its device does not open; it does not skip.

Coverage is measured on the hand-written code; the generated bindings are excluded. The target is 90%
of lines and 80% of branches over everything that runs: CI's legs merged with the GPU machines'
reports from `./eng/test-machine.ps1`, combined by `./eng/merge-coverage.ps1` (which normalises each
machine's source paths, so the platforms combine instead of being counted side by side). CI alone
cannot run the GPU paths, so it gates its own merged legs at 84% / 80%; a change to a GPU path comes
with a `test-machine.ps1` run on the hardware it touches.

`TestCategory("RequiresHardwareDecoder")` marks the bit-exact hardware decode comparisons. Every GPU
machine runs them; CI's macOS runner does not, because its virtualized VideoToolbox decoder does not
reproduce Apple's hardware decoder bit for bit.

Name tests `{Method}_{Scenario}_{ExpectedResult}`. No `Thread.Sleep`. New behaviour needs a test, and
a bug fix needs a test that fails before the fix.

## Code

- Warnings are errors, and the library builds with the recommended analyzer set. Fix the diagnostic
  rather than suppressing it; a suppression needs a comment saying why the rule does not apply.
- Formatting is CSharpier's: `dotnet csharpier format .` before committing. CI checks it.
- The library is Native AOT compatible. CI publishes and runs `samples/FFmpeg.Interop.Smoke` with
  `PublishAot`.
- Hand-written interop uses `LibraryImport` and function pointers; nothing relies on runtime
  marshalling, which the assembly disables.
- APIs that only make sense on one platform carry `[SupportedOSPlatform]`.
- Windows APIs (DXGI, D3D11, D3D12, events) come from CsWin32: list them in
  `src/FFmpeg.Interop/NativeMethods.txt` rather than declaring COM vtables by hand.

### Hardware APIs

`src/FFmpeg.Interop/Hardware/` holds the API-neutral types (`HardwareDevice`, `HardwareFramePool`,
`GpuAdapter`, device routing) and one folder per FFmpeg hwcontext. A hwcontext's folder holds everything
specific to it: its device and surface records, the native calls it needs, and a
`<Api>Extensions` class whose C# `extension` blocks add its members to `HardwareDevice`,
`HardwareFramePool` and `Frame` (`TryGet<Api>...` views, `From<Api>Device`, imports). Supporting a new
one (RKMPP, V4L2 M2M, a vendor API) is a new folder; the core types do not change. Imports build their
frames the way FFmpeg's own pool for that hwcontext does, and hand them over with
`HardwareFramePool.Adopt`.

## The bindings

`src/FFmpeg.Interop/Generated` and the layout tests in `tests/FFmpeg.Interop.Tests/Generated` are
produced by `generate/Generate.cs` from the headers pinned in `generate/inputs.json`. Never edit them
by hand; change the generator and regenerate:

```bash
dotnet run generate/Generate.cs
```

It needs an LLVM install whose clang major matches `ClangMajor` in the script. CI regenerates on
every run and fails if the result differs from what is committed. What the headers do not express
(static inline functions, function-like macros, the per-OS layout of `AVIOContext`) is written by hand
in `src/FFmpeg.Interop/Native`.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/), with a
subject under 50 characters in the imperative mood:

```
fix(decoder): align D3D12 surfaces to the coded size
```

Add a body only when the reason for the change would not be obvious to the next reader. One logical
change per commit.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).
