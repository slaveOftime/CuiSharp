# Rendering

CUI turns every shape into vertices of one format and a few draw commands, so a renderer only needs
one shader. Most apps never think about this: a window picks its renderer and the frame reaches the
screen. This page is for the two cases where you do think about it — choosing the renderer on
purpose, and drawing the pixels yourself.

## Which renderer a window uses

| `CuiAppOptions.Renderer` | Behaviour |
|---|---|
| `Renderer.Auto` (default) | OpenGL 3.3 if the driver offers it, else the built-in software rasterizer |
| `Renderer.OpenGL` | demand a GL context |
| `Renderer.Software` | force the CPU rasterizer — works in VMs and over remote desktop |
| `Renderer.Gpu` | the platform GPU renderer, in builds compiled with one (see below) |

After the window exists, `app.IsSoftwareRenderer` says whether CUI fell back to the software
rasterizer and `app.Backend` names the window layer (`win32`, `x11`, `cocoa`). Log both: they
explain a frame rate nobody can reproduce later.

The shipped `cui.dll` and `libcui` build OpenGL and software only — `native/build-native.ps1`
passes no `-DCUI_D3D12`, `-DCUI_VULKAN` or `-DCUI_METAL`, so in this package `Renderer.Gpu` has
nothing to select. Upstream's `cui_gpu_*` renderers are an opt-in build of the same library, not
something CuiSharp hides: rebuild the native library with the flag and the enum value is already
wired through `CuiAppOptions`.

Your draw code, layout, text shaping and geometry building always run on the CPU; the renderer only
fills triangles. The frame budget is a CPU problem first — the guide's *Frame numbers* has the
measurements.

## Two windowless paths: `CuiSurface`

`CuiSurface` is a CUI context with no window and no message loop; frames are driven by hand, which
is what makes it testable and measurable. It has two kinds, and `Kind` says which you made.

**Software** — `CuiSurface.Create(width, height, scale, threads, theme, accent)`. The same
rasterizer a window falls back to, with the same theme and accent, so a frame drawn here is the
frame the app draws in a window. `surface.Draw(draw, pixels)` builds one frame and rasterizes it
into a byte buffer — `PixelFormat.Bgra` for Win32 DIBs and most framebuffers, `PixelFormat.Rgba`
otherwise; the buffer holds `PixelWidth * PixelHeight * 4` bytes. `PixelWidth` and `PixelHeight`
round `width * scale`, the way CUI computes `cui_draw_data.width_px`; a renderer that sizes its
target by truncating disagrees by a pixel on fractional display scales such as 1.5, and a scaler
comparing the two would refuse every frame. `threads > 1` splits the rasterization across a pool.

`Incremental = true` keeps the previous buffer and repaints only the damaged rectangle, the way a
window does; `LastDamage` is the rectangle the last render touched. A test that compares whole
frames wants all of them painted, so it is off by default. `CommandCount` reads how many draw
commands the last frame produced — useful in an assertion ("a badge is one command").

**GPU** — `CuiSurface.CreateGpu(width, height, in NativeRendererIface textures, scale, theme,
accent)`. No rasterizer runs in the process: CUI produces draw data and you draw it. The texture
callbacks (create, update, destroy) are mandatory — CUI uploads its font atlas through them the
first time it draws text, and every image texture after that. The desktop GL renderer is a
different path: a window, not this method.

## The draw data

`surface.GetDrawData()` returns a pointer to the frame's `NativeDrawData`, valid until the next
`BeginFrame`:

| Field | Meaning |
|---|---|
| `Vertices`, `VertexCount`, `Indices`, `IndexCount` | one interleaved vertex format, triangles |
| `Commands`, `CommandCount` | `NativeDrawCmd`s in draw order |
| `WidthPx`, `HeightPx` | framebuffer size in pixels |
| `ClearColor` | the theme background |
| `FontAtlas` | the font atlas texture id |

Each `NativeDrawCmd` changes at most the scissor (`Clip`, pixels, top-left origin) and the texture
(`0` = the font atlas), plus an index range. A typical frame is a handful of draw calls, and text
never costs a state change per glyph.

`NativeVertex` is 64 bytes:

| Field | Meaning |
|---|---|
| `X`, `Y`, `U`, `V` | position in pixels, texture coordinate |
| `Lx`, `Ly`, `Hw`, `Hh` | position relative to the shape's center; its half size |
| `RTl`, `RTr`, `RBr`, `RBl` | corner radii |
| `Border` | border width (rounded rectangle) or blur sigma (shadow) |
| `Mode` | shading mode, below |
| `Color` | fill color, straight alpha, interpolated |
| `BorderColor` | border color, constant per shape |

Everything except position, texture coordinate, local position and `Color` is the same for every
vertex of a shape and can be `flat` in a shading language.

| `Mode` | Shading |
|---|---|
| `0` — text | `color` × atlas red channel (coverage) |
| `1` — rounded rectangle | signed-distance rounded rectangle, optional border |
| `2` — shadow | Gaussian-blurred rounded rectangle |
| `3` — image | RGBA texture × `color`, masked by the rounded rectangle |
| `4` — solid | `color` (the anti-aliasing is in the geometry) |
| `5` — dither | ordered dither: `BorderColor` where `Border` ≤ the 8×8 Bayer threshold of cell `floor(U, V)` < `Color.A`, masked by the rounded rectangle |

Dither vertices carry the cell grid in `U`, `V`, the density in `Color.A` and a floor in `Border`.
Each cell is either the dot color or nothing, so the dither components (the dither progress bar,
the pattern meter, the veil) look the same in every renderer. A renderer that skips mode 5 still
draws everything else.

## Rules for a renderer

- The output is premultiplied; blend with `ONE`, `ONE_MINUS_SRC_ALPHA`.
- Sample textures with linear filtering and clamp to edge.
- Round the framebuffer size the way `PixelWidth` does, never truncate it.
- Textures arrive through the iface: `TextureCreate(width, height, format, pixels)` returns an id,
  `TextureUpdate` replaces the pixels, `TextureDestroy` frees it. Ids are opaque; `0` is always the
  font atlas.

The worked example is `samples/CuiSharp.Gallery.Browser`: `BrowserHost.cs` drives
`CuiSurface.CreateGpu` with texture thunks and presents `surface.GetDrawData()` once per frame, and
`wwwroot/main.js` is the WebGL2 renderer — the same shaders and blending as upstream's
`cui_render_gl.c`, reading vertices straight out of wasm memory. The second reference is upstream's
software rasterizer, `src/render/cui_render_sw.c`: the same math in plain C. The browser story
itself — the AOT blocker it hit, what is verified — lives in `docs/WASM-NOTES.md`.
