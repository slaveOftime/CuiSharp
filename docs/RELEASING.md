# Releasing

How a version gets from this repository to NuGet. Every command is meant to be pasted; every
number comes from a gate that can fail.

## 1. Decide the version

`Directory.Build.props` holds the single `<Version>` that every package inherits. Pre-1.0 the minor
number carries new surface and the patch carries fixes; the version stays `0.x` until the public
surface is reviewed as a whole. 1.0 is the release where the public surface becomes a
promise.

```xml
<Version>0.1.0</Version>
```

## 2. Run every gate

```sh
dotnet build CuiSharp.slnx -c Release -warnaserror
dotnet test  CuiSharp.slnx -c Release
dotnet format CuiSharp.slnx --verify-no-changes
pwsh tools/regen.ps1 -NoNative -NoDiff
pwsh tools/api-surface.ps1                      # then review the diff of docs/API-SURFACE.txt
pwsh tools/verify-template.ps1                  # dotnet new, build, test, AOT, dotnet cui
dotnet publish samples/CuiSharp.Gallery -c Release -r win-x64
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900
```

What each one protects:

The Linux native build must run inside the declared ABI baseline (`debian:11`, glibc 2.31), which is
what `release.yml` and `ci.yml` pin; `build-native.sh` fails if the produced `libcui.so` needs more.

| Gate | Fails when |
|---|---|
| `-warnaserror` | a warning appeared, including a nullable or doc-comment regression |
| `dotnet test` | behaviour changed; includes ABI, enum contract, frame allocation and doc drift tests |
| `format --verify-no-changes` | formatting drifted (`dotnet format CuiSharp.slnx` fixes it) |
| `regen -NoDiff` | a generated file is stale, or the generator's own 13 contract checks broke |
| `api-surface.ps1` | the public surface changed — read the diff, that is the review |
| `verify-template.ps1` | what a user gets from `dotnet new` no longer builds, tests, publishes, or is drivable |
| AOT publish | an IL2xxx/IL3xxx warning appeared |
| `--fps 900` | the mean frame time is more than 10 % over the budget in [PERFORMANCE.md](PERFORMANCE.md), or allocation is not 0 B |

## 3. Regenerate the face of the release

```sh
pwsh tools/snapshots.ps1                                          # light, twenty-eight pages
pwsh tools/snapshots.ps1 -Theme dark -Suffix -dark -Pages overview,charts,studio,data
```

The snapshots in `docs/images/gallery` are what the README shows and what a person compares against
their own machine, so they are taken from the app — never edited. Three of them photograph the
machine they ran on: the calendar's today, the year of activity, and the directory the file browser
opens, so their pictures move with the date and the checkout.

## 4. Update the record

- `CHANGELOG.md`: give the version its section, `## [x.y.z] — unreleased` while the work is going
  in and `## [x.y.z] — date` once the release is cut. User-facing facts only (new API, behaviour
  changes, fixes), each with the migration if there is one; detail stays in the pull request. This
  section *is* the release page: `release.yml` runs `tools/release-notes.ps1` and publishes it
  verbatim, so a version with no section — or one still marked unreleased — fails the pack job,
  before anything is pushed to nuget.org. Check it locally with
  `pwsh tools/release-notes.ps1 -Version x.y.z`.
- `skills/cui-dotnet/{llms.txt,api/*.md}` are generated from the headers; check them in the `regen`
  diff rather than editing them by hand. `SKILL.md` beside them is hand-written.

## 5. Pack and inspect

```sh
dotnet pack CuiSharp.slnx -c Release -o artifacts/packages
```

Then open the packages, because a package that looks right in a file listing can still be wrong:

```sh
# the native library, the analyzers and the targets must be inside the main package
unzip -l artifacts/packages/CuiSharp.*.nupkg | grep -E 'cui\.(dll|lib)|analyzers/|build/'

# the template must carry the whole solution, not just one file
unzip -l artifacts/packages/CuiSharp.Templates.*.nupkg | head -20
```

| Package | Must contain |
|---|---|
| `CuiSharp` | `lib/net10.0/CuiSharp.dll`, each RID's `runtimes/<rid>/native/` files, `runtimes/native-provenance.txt` (revision, compiler, ABI, hashes — see [native-abi.md](../skills/cui-dotnet/native-abi.md)), `analyzers/dotnet/cs/CuiSharp.Analyzers.dll`, `analyzers/dotnet/cs/CuiSharp.CodeFixes.dll`, `build/CuiSharp.targets`, `README.md` |
| `CuiSharp.Testing` | `lib/net10.0/CuiSharp.Testing.dll` and a dependency on `CuiSharp` |
| `CuiSharp.Tools` | a `DotnetToolSettings.xml` with `CommandName=cui` |
| `CuiSharp.Templates` | `content/CuiSharp.App/…` including the app, its tests and `AGENTS.md` |

`dotnet pack` warns when no native artifacts are found, because a managed-only package installs
cleanly and then fails at the first frame. Release packing must assemble all five RIDs from their
own runners, including the Windows/Linux ARM64 legs. Separate RID directories prevent same-named
archives from replacing each other, and a `rid=` stamp that disagrees with the package path fails
the pack instead of mislabelling an archive.

## 6. Smoke-test the packages the way a user gets them

```sh
mkdir /tmp/nuget-smoke && cd /tmp/nuget-smoke
dotnet new console -n Smoke && cd Smoke
dotnet add package CuiSharp -s <repo>/artifacts/packages
# paste the README's five-line example, then:
dotnet run                       # a window opens
dotnet publish -c Release -r <rid> -p:PublishAot=true
ls bin/Release/net10.0/<rid>/publish/     # one executable, no native library beside it
```

A publish for a RID the package does not carry, or without its archive, is an error naming the RID
and the path — that is the package refusing to ship a binary that would fail at start.

Then the template, from the same local feed:

```sh
dotnet new install <repo>/artifacts/packages/CuiSharp.Templates.*.nupkg
dotnet new cui-app -n FreshApp && cd FreshApp && dotnet test
dotnet tool install --add-source <repo>/artifacts/packages -g CuiSharp.Tools
cui tree --project CuiSharp.App --no-build
```

## 7. Publish

`.github/workflows/release.yml` does this on a `v*.*.*` tag: it packs, verifies the package
contents, and pushes with NuGet trusted publishing (OIDC), so no key is stored in the repository.
Tagging is the whole act:

```sh
git tag -a v0.1.0 -m "CuiSharp 0.1.0" && git push --tags
```

To publish by hand instead — a local dry run, or a yanked release — the order matters:
`CuiSharp` first, then `Testing`, `Tools`, `Templates`, because NuGet rejects a package whose
dependency is not there yet. Symbol packages (`.snupkg`) go in the same push.

```sh
dotnet nuget push "artifacts/packages/*.nupkg" -s https://api.nuget.org/v3/index.json -k $NUGET_API_KEY
```

## 8. After

- Check the NuGet page renders the README and the version is listed.
- `dotnet tool update -g CuiSharp.Tools` on a clean machine and run `cui tree` once.
- Record the numbers the gates printed in the release notes, next to the version.
