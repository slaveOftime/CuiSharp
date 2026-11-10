# Native side

Everything that concerns the C library lives here: the submodule and the build
scripts. The binding itself is generated from these headers by
`tools/bindgen.cs` — see [bindings/README.md](../bindings/README.md).

```
CUI/               git submodule, pinned to a CUI release
build-native.ps1   Windows x64/ARM64: builds cui.dll and cui.lib
build-native.sh    Linux x64/ARM64 or macOS ARM64: shared library and libcui.a
build-native-container.sh  Linux: builds inside debian:11 (glibc 2.31)
```

## Build

```sh
git submodule update --init --recursive
pwsh native/build-native.ps1     # Windows
./native/build-native.sh         # Linux (needs cmake, build-essential, libx11-dev)
```

Binaries are **not committed**; they are derived from the submodule. Windows selects the host
OS's MSVC architecture (override with `-Architecture x64` or `arm64`); Linux builds natively for
the container's CPU. CI and release use a separate native runner per RID, architecture-specific
cache keys, and a cold native cache whenever the build scripts change.

For release packing, keep each RID's files and provenance in a separate directory; a shared
`cui.lib` or `libcui.a` filename does not identify its architecture.

| Artifact | Used by | Why |
|---|---|---|
| `cui.dll` | `dotnet run`, `dotnet watch`, Debug builds | loaded at runtime; the fast iteration path |
| `libcui.so` | the same on Linux | ditto |
| `cui.lib` | `dotnet publish -r win-x64` / `win-arm64` | static archive linked into the NativeAOT exe |
| `libcui.a` | `dotnet publish -r linux-x64` / `linux-arm64` / `osx-arm64` | ditto |

Windows builds use `/MT`, so nothing needs a VC++ redistributable. The only
external requirement is the Universal CRT, which ships with Windows 10 and later.

Running the gallery does not need the static archives, and publishing without a
`RuntimeIdentifier` produces a normal framework-dependent app.

## Bumping the submodule

The binding is generated, so a CUI release is picked up with one command:

```sh
git -C native/CUI fetch --tags && git -C native/CUI checkout vX.Y.Z
pwsh tools/regen.ps1        # native build -> bindgen -> build + tests
git diff bindings/api.json  # exactly what upstream changed
```

`GeneratedAbiTests` compares every managed struct against libclang's own
`sizeof`/`offsetof`, so a struct that changed upstream fails the build before it
can corrupt a frame. No MSVC installation is needed for that check: the
generator parses the headers with the libclang that ships in a NuGet package,
using the minimal C standard headers in `tools/clang-include`.

## Pristine upstream

`native/CUI/` stays at a clean upstream revision. There are no local native patches: native
behaviour belongs upstream, not in a build copy. See D2 in [ARCHITECTURE.md](../docs/ARCHITECTURE.md).

Overrides for the generated binding (renames, promoted anonymous enums,
`[SuppressGCTransition]`) live in `bindings/overrides.json`, not here.
