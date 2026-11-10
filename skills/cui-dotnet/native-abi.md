# The native ABI a package promises

A CuiSharp package carries a prebuilt CUI for each supported RID. Those binaries are built once,
on one image, and then load on whatever machine installs the package — so the ABI they were built
against is a promise, not an accident of the build machine.

## Promises

| RID | Native files | Built with | Minimum runtime |
|---|---|---|---|
| `win-x64` | `cui.dll`, `cui.lib` | MSVC v143 (VS 2022), `/MT` static CRT | Windows 10 x64, no VC++ redistributable |
| `linux-x64` | `libcui.so`, `libcui.a` | `ubuntu-22.04`, glibc **2.35** | glibc 2.35 (Ubuntu 22.04, Debian 12), Xlib 1.6 for the x11 backend |
| `osx-arm64` | `libcui.dylib`, `libcui.a` | `macos-latest`, `CMAKE_OSX_DEPLOYMENT_TARGET=12.0` | macOS 12 (Monterey) on Apple silicon |

A glibc-built `.so` records the *newest* versioned symbol it uses, and a loader older than that
refuses the whole library: `version 'GLIBC_2.38' not found`. Building the Linux binaries on a newer
image therefore narrows the promise without anyone choosing it. `native/build-native.sh` refuses to
emit a `libcui.so` that needs a newer glibc than `CUI_GLIBC_BASELINE` (default `2.35`), and writes
what it measured into `native/native-provenance.txt`.

Raising the baseline is a deliberate act: change the default in the script, the table above and the
CI image together, and say why in the release notes.

## What the package records

Every `CuiSharp` package contains `runtimes/native-provenance.txt`, generated at pack time:

```text
CuiSharp 0.1.0 native artifacts
win-x64 cui.dll 24EDF8DB…348C
win-x64 cui.lib FEB51ECF…1C03
repository https://github.com/yappologistic/CuiSharp @ b27a909…
cui-rev=<CUI submodule revision>
os=Linux-x86_64
built-on=Ubuntu 22.04.4 LTS
cc=gcc (Ubuntu 11.4.0-1ubuntu1~22.04) 11.4.0
requires-glibc=2.35
baseline-glibc=2.35
file=libcui.so sha256=…
```

The hashes come from the files as packed, so a consumer that wants to know whether two machines are
running the same CUI has an answer that does not depend on a file listing.

## How a consumer sees the failure early

`build/CuiSharp.targets` matches the RID exactly and fails a NativeAOT publish that cannot be one:

- a RID the package has no archive for (`linux-arm64`, `linux-musl-x64`) is an error naming the
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
(`ubuntu-22.04`) as well as Windows, with the native library coming only from the package.
