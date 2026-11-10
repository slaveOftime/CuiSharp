# The native ABI a package promises

A CuiSharp package carries a prebuilt CUI for each supported RID. Those binaries are built once,
on one image, and then load on whatever machine installs the package — so the ABI they were built
against is a promise, not an accident of the build machine.

## Promises

| RID | Native files | Built with | Minimum runtime |
|---|---|---|---|
| `win-x64` | `cui.dll`, `cui.lib` | MSVC v143 (VS 2022), `/MT` static CRT | Windows 10 x64, no VC++ redistributable |
| `win-arm64` | `cui.dll`, `cui.lib` | native `windows-11-arm` runner, MSVC ARM64, `/MT` | Windows 11 ARM64, no VC++ redistributable |
| `linux-x64` | `libcui.so`, `libcui.a` | `debian:11`, glibc **2.31** | glibc 2.31 (Debian 11, Ubuntu 20.04), Xlib 1.6 for the x11 backend |
| `linux-arm64` | `libcui.so`, `libcui.a` | native `ubuntu-24.04-arm` runner, `debian:11` ARM64, glibc **2.31** | glibc 2.31 on ARM64, Xlib for the x11 backend |
| `osx-arm64` | `libcui.dylib`, `libcui.a` | `macos-latest`, `CMAKE_OSX_DEPLOYMENT_TARGET=12.0` | macOS 12 (Monterey) on Apple silicon |

The Windows/Linux ARM64 legs build and pass their gates on hosted `windows-11-arm` and
`ubuntu-24.04-arm` runners. Each RID is compiled on its own CPU/OS runner, never populated by
copying an x64 archive. A local pack labels the flat native build using the SDK's RID (override with
`CuiNativeRid` when needed).
Release packing supplies separate `CuiNativeWinDir`, `CuiNativeWinArm64Dir`, `CuiNativeLinuxDir`,
`CuiNativeLinuxArm64Dir` and `CuiNativeOsxDir`; the package records each RID's own compiler metadata
and hashes. The build's `rid=` stamp is validated against the package path, so a cross-built
archive or an emulated SDK cannot silently label ARM64 bytes as x64.

A glibc-built `.so` records the *newest* versioned symbol it uses, and a loader older than that
refuses the whole library: `version 'GLIBC_2.38' not found`. Building the Linux binaries on a newer
image therefore narrows the promise without anyone choosing it. `native/build-native.sh` refuses to
emit a `libcui.so` that needs a newer glibc than `CUI_GLIBC_BASELINE` (default `2.31`), and writes
what it measured into `native/native-provenance.txt`.

The Linux binaries are built by `native/build-native-container.sh` inside the image named above, not
on the runner: glibc merges and moves symbols between releases (libpthread's are in libc from 2.34),
so a library linked on a newer image refuses to load on an older one. Raising the baseline is a
deliberate act: change the default in the script, the image in the workflows, the table above and
`CuiNative.LinuxFloor` together — a test fails when the last two disagree — and say why in the
release notes.

## What the package records

Every `CuiSharp` package contains `runtimes/native-provenance.txt`, generated at pack time:

```text
CuiSharp 0.1.0 native artifacts
win-x64 cui.dll 24EDF8DB…348C
win-x64 cui.lib FEB51ECF…1C03
repository https://github.com/yappologistic/CuiSharp @ b27a909…
cui-rev=<CUI submodule revision>
os=Linux-x86_64
built-on=Debian GNU/Linux 11 (bullseye)
cc=gcc (Debian 10.2.1-6) 10.2.1 20210110
requires-glibc=2.31
baseline-glibc=2.31
file=libcui.so sha256=…
```

The hashes come from the files as packed, so a consumer that wants to know whether two machines are
running the same CUI has an answer that does not depend on a file listing.

## How a consumer sees the failure early

`build/CuiSharp.targets` matches the RID exactly and fails a NativeAOT publish that cannot be one:

- a RID the package has no archive for (`linux-musl-arm64`, `linux-musl-x64`) is an error naming the
  supported set, instead of linking the wrong architecture's archive;
- a missing archive for a supported RID is an error, instead of a publish that silently depends on
  the shared library and then deletes it;
- an AOT publish that linked statically has `cui.dll`, `cui.lib`, `libcui.so`, `libcui.dylib` and
  `libcui.a` removed from the output directory, on every platform, so "one file" stays true.

## Clean-consumer check

The release gates ask for the package to be proven in a consumer that does not rebuild it:

```sh
dotnet pack src/CuiSharp/CuiSharp.csproj -c Release -o artifacts/packages
dotnet new console -n Smoke -o /tmp/smoke && cd /tmp/smoke
dotnet add package CuiSharp --prerelease -s <repo>/artifacts/packages
dotnet run                                    # JIT: loads runtimes/<rid>/native/
dotnet publish -c Release -r <rid>            # AOT: links the archive, one executable
```

CI runs exactly that in `ci.yml`'s `packaged-consumer` job, on the oldest supported Linux image
(`ubuntu-22.04`) as well as Windows. ARM64 legs use native Windows/Linux ARM64 runners, with the
native library coming only from the package.
