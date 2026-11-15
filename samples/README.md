# File-based samples

Four self-contained CuiSharp apps, one `.cs` file each. They all start with
`#:package CuiSharp@*`, so every restore resolves the latest stable package, and
the same run / publish / headless recipe. The icon beside each file is what the
published window uses.

| File | What it shows |
|---|---|
| [`disk.cs`](disk.cs) | A treemap of a folder on disk: sizes, scan progress, ranking and free space. |
| [`stock.cs`](stock.cs) | A global market-cap dashboard over Tencent + Eastmoney, with a live tracker and cache. |
| [`music.cs`](music.cs) | A music player over your folders: library, transport, seek bar and real-audio visualizers. |
| [`download.cs`](download.cs) | A magnet + HTTP download manager: newest-first queue, peers, speed and a details pane. |

## Run

```sh
dotnet run disk.cs
dotnet run stock.cs
dotnet run music.cs
dotnet run download.cs
```

`disk.cs` also takes folders: `dotnet run disk.cs -- C:\Windows "C:\Program Files"`.

## Publish

Each publish leaves one native executable beside its source, with no .NET runtime on the target:

```sh
dotnet publish disk.cs -c Release -o .
dotnet publish stock.cs -c Release -o .
dotnet publish music.cs -c Release -o .
dotnet publish download.cs -c Release -o .
```

The generated `.exe` files are gitignored. `music.cs` targets Windows because its player is the
WinRT `MediaPlayer`; the other three publish for the current platform.

## Drive a published build headlessly

```sh
dotnet publish disk.cs -c Release -p:CuiSharpHeadless=true -o artifacts/disk
dotnet cui session --app artifacts/disk/disk.exe < steps.txt
```

A published build can be inspected at any viewport, with its state kept away from the real profile:

```sh
dotnet cui png shot.png --app artifacts/disk/disk.exe --size 420x720
dotnet cui png shot.png --app artifacts/disk/disk.exe --sizes 420x720,1180x820
dotnet cui tree --app artifacts/disk/disk.exe --state artifacts/cui-run
```

The apps read `CUISHARP_STATE_HOME`: `music` uses `<root>/CuiMusic`, `download` uses
`<root>/CuiDownload`, and `stock` uses `<root>/GlobalMarketCap`. `CuiSharp@*` must resolve a package
that carries the `CUISHARP_VIEWPORT` override; with an older build, `--size` refuses rather than
reporting a size it did not get.

`dotnet cui` is the companion tool (`dotnet tool install -g CuiSharp.Tools`). Every app repeats the
full recipe at the top of its file:

```text
dotnet cui tree                  the controls the last frame drew
dotnet cui click Up              click one, print the frame it produced
dotnet cui png shot.png          that frame as a PNG
dotnet cui docs read live-debugging
dotnet cui session < steps.txt
```

Use `dotnet tool update -g CuiSharp.Tools` to keep the tool current. On Windows PowerShell, pipe the
script instead of using input redirection:

```powershell
Get-Content steps.txt | dotnet cui session --app artifacts/disk/disk.exe
```

`CuiSharp@*` and the tool are versioned separately: restoring the samples always picks the latest
stable package, while the tool only changes when you update it.
