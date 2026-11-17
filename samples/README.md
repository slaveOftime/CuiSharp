# File-based samples

Four self-contained CuiSharp apps, one `.cs` file each. They start with
`#:project ../src/CuiSharp/CuiSharp.csproj`, so each one builds against the source in this
repository — including its analyzers — and they share the same run / publish / headless recipe.
The icon beside each file is what the published window uses.

| File | What it shows |
|---|---|
| [`disk.cs`](disk.cs) | A treemap of a folder on disk: sizes, scan progress, ranking and free space. |
| [`stock.cs`](stock.cs) | A global market-cap dashboard over Tencent + Eastmoney, with a live tracker and cache. |
| [`music.cs`](music.cs) | A music player over your folders: library, transport, seek bar and real-audio visualizers. |
| [`download.cs`](download.cs) | A magnet + HTTP download manager: newest-first queue, peers, speed and a details pane. |

All four call `ui.UseSystemFontFallbacks()` once at startup: the bundled faces are Latin, and a
folder name, a download name or a music tag can be written in any script — or carry an emoji — so
the apps install the faces the machine has instead of drawing those glyphs as nothing.

## Run

```sh
dotnet run disk.cs
dotnet run stock.cs
dotnet run music.cs
dotnet run download.cs
```

`disk.cs` also takes folders: `dotnet run disk.cs -- <folder> [folder...]`.

## Publish

Each publish leaves one native executable beside its source, with no .NET runtime on the target:

```sh
dotnet publish disk.cs -c Release -o .
dotnet publish stock.cs -c Release -o .
dotnet publish music.cs -c Release -o .
dotnet publish download.cs -c Release -o .
```

The generated binaries are gitignored. All four publish for the current platform. `music.cs` is one
NativeAOT executable plus the two native libraries it loads: `miniaudioex.dll` and, on Windows,
`vcruntime140.dll`. Windows Sandbox carries no VC++ redistributable, so the runtime travels with the
app. The exe alone will not play: publish all three together.

## Drive a published build headlessly

```sh
dotnet publish disk.cs -c Release -p:CuiSharpHeadless=true -o artifacts/disk
dotnet cui session --app artifacts/disk/disk --script steps.txt
```

The published app is `disk.exe` on Windows and `disk` elsewhere. A published build can be inspected
at any viewport, with its state kept away from the real profile:

```sh
dotnet cui png shot.png --app artifacts/disk/disk --size 420x720
dotnet cui png shot.png --app artifacts/disk/disk --sizes 420x720,1180x820
dotnet cui tree --app artifacts/disk/disk --state artifacts/cui-run
```

The apps read `CUISHARP_STATE_HOME`: `music` uses `<root>/CuiMusic`, `download` uses
`<root>/CuiDownload`, and `stock` uses `<root>/GlobalMarketCap`. The `#:project` reference means a
sample always builds with the `CUISHARP_VIEWPORT` override, so `--size` reports the viewport it
really used.

`dotnet cui` is the companion tool (`dotnet tool install -g CuiSharp.Tools`). Every app repeats the
full recipe at the top of its file:

```text
dotnet cui tree                  the controls the last frame drew
dotnet cui click Up              click one, print the frame it produced
dotnet cui png shot.png          that frame as a PNG
dotnet cui docs read live-debugging
dotnet cui session --script steps.txt
```

Use `dotnet tool update -g CuiSharp.Tools` to keep the tool current. `--script` works in every
shell; a POSIX shell can still pipe the same file into `dotnet cui session` if it prefers.

`#:project` and the tool are versioned separately: a sample always builds against this repository,
while the tool only changes when you update it.
