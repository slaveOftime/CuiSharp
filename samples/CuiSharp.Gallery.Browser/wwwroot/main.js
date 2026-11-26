import { dotnet } from './_framework/dotnet.js';

// ---- WebGL2 renderer --------------------------------------------------------
// A port of CUI's own web renderer (native/CUI/web/site/js/gl.js, itself a port
// of src/render/cui_render_gl.c): the same shaders, the same blending and
// scissoring, the same texture handling. The one difference is where the draw
// data lives: CUI's module exposes helper exports for it, while here the .NET
// host hands over the cui_draw_data pointer and this code reads the struct
// straight out of the wasm heap (wasm32 layout: 4-byte pointers).

const VS = `#version 300 es
precision highp float; precision highp int;
in vec2 a_pos; in vec2 a_uv; in vec2 a_local; in vec2 a_half; in vec4 a_radii; in vec2 a_params; in vec4 a_color; in vec4 a_bcolor;
uniform vec2 u_viewport;
out vec2 v_uv; out vec2 v_local; flat out vec2 v_half; flat out vec4 v_radii; flat out vec2 v_params; out vec4 v_color; flat out vec4 v_bcolor;
void main() {
  v_uv = a_uv; v_local = a_local; v_half = a_half; v_radii = a_radii;
  v_params = a_params; v_color = a_color; v_bcolor = a_bcolor;
  gl_Position = vec4(a_pos.x / u_viewport.x * 2.0 - 1.0, 1.0 - a_pos.y / u_viewport.y * 2.0, 0.0, 1.0);
}`;

const FS = `#version 300 es
precision highp float; precision highp int;
in vec2 v_uv; in vec2 v_local; flat in vec2 v_half; flat in vec4 v_radii; flat in vec2 v_params; in vec4 v_color; flat in vec4 v_bcolor;
uniform sampler2D u_tex;
out vec4 frag;
float sd_rrect(vec2 p, vec2 b, vec4 r) {
  float rr = p.x < 0.0 ? (p.y < 0.0 ? r.x : r.w) : (p.y < 0.0 ? r.y : r.z);
  rr = min(rr, min(b.x, b.y));
  vec2 q = abs(p) - b + rr;
  return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - rr;
}
float erf_(float x) {
  float s = sign(x); x = abs(x);
  float t = 1.0 / (1.0 + 0.3275911 * x);
  float y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * exp(-x * x);
  return s * y;
}
void main() {
  int mode = int(v_params.y + 0.5);
  vec4 c = v_color;
  if (mode == 4) { frag = vec4(c.rgb * c.a, c.a); return; }
  if (mode == 0) { float a = c.a * texture(u_tex, v_uv).r; frag = vec4(c.rgb * a, a); return; }
  float d = sd_rrect(v_local, v_half, v_radii);
  if (mode == 1) {
    float outer = clamp(0.5 - d, 0.0, 1.0);
    float bw = v_params.x;
    float inner = bw > 0.0 ? clamp(0.5 - (d + bw), 0.0, 1.0) : outer;
    float fa = c.a * inner, ba = v_bcolor.a * (outer - inner);
    frag = vec4(c.rgb * fa + v_bcolor.rgb * ba, fa + ba);
  } else if (mode == 2) {
    float s = max(v_params.x, 0.01);
    float a = c.a * 0.5 * (1.0 - erf_(d / (s * 1.41421356)));
    frag = vec4(c.rgb * a, a);
  } else {
    float cov = clamp(0.5 - d, 0.0, 1.0);
    vec4 t = texture(u_tex, v_uv);
    float k = c.a * cov;
    frag = vec4(t.rgb * t.a * c.rgb * k, t.a * k);
  }
}`;

const VERTEX_SIZE = 64;   // sizeof(cui_vertex)
const CMD_SIZE = 28;      // sizeof(cui_draw_cmd) on wasm32: 4-byte pointers

class GLRenderer {
  constructor(canvas, keepDrawingBuffer) {
    const gl = canvas.getContext('webgl2', {
      alpha: false, antialias: false, depth: false, stencil: false,
      premultipliedAlpha: true, preserveDrawingBuffer: keepDrawingBuffer,
    });
    if (!gl) throw new Error('WebGL2 is not available');
    this.gl = gl;
    this.textures = new Map();
    this.nextTex = 1;
    const sh = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error('CUI shader: ' + gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    const vs = sh(gl.VERTEX_SHADER, VS), fs = sh(gl.FRAGMENT_SHADER, FS);
    gl.attachShader(p, vs);
    gl.attachShader(p, fs);
    ['a_pos', 'a_uv', 'a_local', 'a_half', 'a_radii', 'a_params', 'a_color', 'a_bcolor'].forEach((n, i) => gl.bindAttribLocation(p, i, n));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error('CUI program: ' + gl.getProgramInfoLog(p));
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    this.program = p;
    this.uViewport = gl.getUniformLocation(p, 'u_viewport');
    this.uTex = gl.getUniformLocation(p, 'u_tex');
    this.vao = gl.createVertexArray();
    this.vbo = gl.createBuffer();
    this.ebo = gl.createBuffer();
    gl.bindVertexArray(this.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.vbo);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.ebo);
    const attr = (i, n, type, norm, off) => { gl.enableVertexAttribArray(i); gl.vertexAttribPointer(i, n, type, norm, VERTEX_SIZE, off); };
    attr(0, 2, gl.FLOAT, false, 0);    // x, y
    attr(1, 2, gl.FLOAT, false, 8);    // u, v
    attr(2, 2, gl.FLOAT, false, 16);   // lx, ly
    attr(3, 2, gl.FLOAT, false, 24);   // hw, hh
    attr(4, 4, gl.FLOAT, false, 32);   // radii
    attr(5, 2, gl.FLOAT, false, 48);   // border, mode
    attr(6, 4, gl.UNSIGNED_BYTE, true, 56);   // color
    attr(7, 4, gl.UNSIGNED_BYTE, true, 60);   // border color
    gl.bindVertexArray(null);
  }

  // texture callbacks, called from wasm through BrowserHost's thunks
  createTexture(w, h, fmt, px) {
    const gl = this.gl, t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
    // CUI creates empty textures with a null pixel pointer and fills them in through
    // texture_update later (a fresh emoji atlas does exactly that), so null has to stay
    // null: a view starting at wasm address 0 would upload the heap's first bytes as pixels.
    if (fmt === 1) gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, w, h, 0, gl.RED, gl.UNSIGNED_BYTE, px ? heapBytes(px, w * h) : null);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, px ? heapBytes(px, w * h * 4) : null);
    const id = this.nextTex++;
    this.textures.set(id, { t, fmt });
    return id;
  }
  updateTexture(id, x, y, w, h, px, stride) {
    const gl = this.gl, e = this.textures.get(id);
    if (!e) return;
    gl.bindTexture(gl.TEXTURE_2D, e.t);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, stride / e.fmt);
    // px addresses the first pixel of the damaged rect, not the start of a full row of it,
    // so the span ends at the last pixel of the last row. stride * h would read up to
    // x * fmt bytes past the end of the source for a rect that is not flush left - and
    // throw outright when the source ends near the top of the wasm heap.
    gl.texSubImage2D(gl.TEXTURE_2D, 0, x, y, w, h, e.fmt === 1 ? gl.RED : gl.RGBA, gl.UNSIGNED_BYTE,
      heapBytes(px, (h - 1) * stride + w * e.fmt));
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
  }
  destroyTexture(id) {
    const e = this.textures.get(id);
    if (e) { this.gl.deleteTexture(e.t); this.textures.delete(id); }
  }

  // draws the cui_draw_data at `dd` (a wasm heap pointer)
  draw(dd, canvas) {
    const gl = this.gl, buf = heapBuffer();
    const i32 = new Int32Array(buf), u32 = new Uint32Array(buf), f32 = new Float32Array(buf);
    const verts = u32[dd >> 2], vcount = i32[(dd + 4) >> 2];
    const idx = u32[(dd + 8) >> 2], icount = i32[(dd + 12) >> 2];
    const cmds = u32[(dd + 16) >> 2], ccount = i32[(dd + 20) >> 2];
    const W = i32[(dd + 24) >> 2], H = i32[(dd + 28) >> 2];
    const cc = u32[(dd + 32) >> 2];       // r | g<<8 | b<<16, straight from cui_color
    const atlas = u32[(dd + 36) >> 2];
    // The drawing buffer has to match the frame CUI laid out or the viewport clips it. Follow
    // the draw data instead of trusting the resize path to agree on how a fractional display
    // scale rounds: a backing store resized here is valid at once, and the alternative -
    // refusing the frame - loses the whole canvas over a one-pixel disagreement.
    if (canvas.width !== W || canvas.height !== H) { canvas.width = W; canvas.height = H; }
    gl.viewport(0, 0, W, H);
    gl.clearColor((cc & 255) / 255, ((cc >> 8) & 255) / 255, ((cc >> 16) & 255) / 255, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    if (!icount) return;
    gl.enable(gl.BLEND);
    gl.blendEquation(gl.FUNC_ADD);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.disable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    gl.enable(gl.SCISSOR_TEST);
    gl.useProgram(this.program);
    gl.uniform2f(this.uViewport, W, H);
    gl.uniform1i(this.uTex, 0);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindVertexArray(this.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.vbo);
    gl.bufferData(gl.ARRAY_BUFFER, heapBytes(verts, vcount * VERTEX_SIZE), gl.STREAM_DRAW);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.ebo);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, heapBytes(idx, icount * 4), gl.STREAM_DRAW);
    let bound = -1;
    for (let i = 0; i < ccount; i++) {
      const base = cmds + i * CMD_SIZE;
      const count = u32[(base + 24) >> 2];
      if (!count) continue;
      const cx = f32[base >> 2], cy = f32[(base + 4) >> 2], cw = f32[(base + 8) >> 2], ch = f32[(base + 12) >> 2];
      const x0 = Math.floor(cx), y0 = Math.floor(cy), x1 = Math.ceil(cx + cw), y1 = Math.ceil(cy + ch);
      if (x1 <= x0 || y1 <= y0) continue;
      gl.scissor(x0, H - y1, x1 - x0, y1 - y0);
      const tid = u32[(base + 16) >> 2] || atlas;
      if (tid !== bound) { const e = this.textures.get(tid); gl.bindTexture(gl.TEXTURE_2D, e ? e.t : null); bound = tid; }
      gl.drawElements(gl.TRIANGLES, count, gl.UNSIGNED_INT, u32[(base + 20) >> 2] * 4);
    }
    gl.disable(gl.SCISSOR_TEST);
    gl.bindVertexArray(null);
  }

  dispose() {
    const gl = this.gl;
    for (const e of this.textures.values()) gl.deleteTexture(e.t);
    this.textures.clear();
    gl.deleteBuffer(this.vbo); gl.deleteBuffer(this.ebo);
    gl.deleteVertexArray(this.vao); gl.deleteProgram(this.program);
  }
}

function heapBuffer() { return globalThis.getDotnetRuntime(0).Module.HEAPU8.buffer; }
function heapBytes(ptr, len) { return new Uint8Array(heapBuffer(), ptr, len); }

// ---- page -------------------------------------------------------------------

const canvas = document.querySelector('#surface');
const keyboardSink = document.querySelector('#keyboard-sink');
const status = document.querySelector('#status');
let context = null;    // 2d in software mode, set once the renderer is chosen
let gpu = null;        // GLRenderer in gpu mode
let host;
let imageData;
let imageMemory;
let imagePointer = -1;
let imageWidth = 0;
let imageHeight = 0;
const query = new URLSearchParams(location.search);
const smoke = query.has('smoke');
const wantGpu = query.get('renderer') !== 'software';
const measureFrames = query.has('perf');
// keep the drawing buffer readable between frames: the CI smoke test and
// page screenshots read pixels from CDP after the frame has composited
const keepBuffer = smoke || query.has('shots');
const frameCosts = [];
const frameIntervals = [];
let previousFrameTimestamp = 0;

// CUI calls these from wasm when a widget needs a GPU texture (the font atlas,
// page images); they only exist in gpu mode and only touch the GL renderer.
const textures = {
  create(w, h, fmt, px) { return gpu ? gpu.createTexture(w, h, fmt, px) : 0; },
  update(t, x, y, w, h, px, stride) { gpu?.updateTexture(t, x, y, w, h, px, stride); },
  destroy(t) { gpu?.destroyTexture(t); },
};

// CUI has no access to system font files in wasm. Rasterize its emoji requests with
// the browser's system font fallback and copy straight-alpha RGBA into the CUI atlas.
const utf8 = new TextDecoder();
let emojiContext;
function drawEmoji(pointer, length, px, out) {
  // A selection from the picker is inserted by managed gallery code, not by a
  // browser input event. Warm the text fallback as soon as the picker draws.
  loadEmojiFallback();
  emojiContext ??= document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  if (!emojiContext) return false;
  const c = emojiContext;
  if (c.canvas.width !== px || c.canvas.height !== px) c.canvas.width = c.canvas.height = px;
  c.clearRect(0, 0, px, px);
  c.font = `${Math.round(px * 0.84)}px "Apple Color Emoji", "Segoe UI Emoji", "Noto Color Emoji", "Twemoji Mozilla", sans-serif`;
  c.textAlign = 'center';
  c.textBaseline = 'middle';
  c.fillText(utf8.decode(heapBytes(pointer, length)), px / 2, px * 0.54);
  const pixels = c.getImageData(0, 0, px, px).data;
  for (let i = 3; i < pixels.length; i += 4) {
    if (!pixels[i]) continue;
    heapBytes(out, pixels.length).set(pixels);
    return true;
  }
  return false;
}

// Browsers anchor composition to a focused DOM editor. CUI reports its caret in
// canvas points (CSS pixels), not bitmap pixels; focus the proxy only while editing.
let caret = null;
let composing = false;
function placeIme() {
  const active = document.activeElement;
  if (!caret) {
    if (active === keyboardSink && !composing) canvas.focus({ preventScroll: true });
    return;
  }
  const rect = canvas.getBoundingClientRect();
  const height = Math.max(1, caret.height);
  Object.assign(keyboardSink.style, {
    left: `${Math.max(0, Math.min(innerWidth - 1, rect.left + caret.x))}px`,
    top: `${Math.max(0, Math.min(innerHeight - height, rect.top + caret.y))}px`,
    height: `${height}px`,
    lineHeight: `${height}px`,
    fontSize: `${Math.max(10, Math.round(height * 0.75))}px`,
  });
  if (active === canvas) keyboardSink.focus({ preventScroll: true });
}

function setCaret(x, y, height, focused) {
  caret = focused ? { x, y, height } : null;
  placeIme();
}

// Text is drawn by CUI, not CSS. Download a known SFNT face only when needed and
// install it between frames; never block startup or defer input to a different field.
let cjkFont;
let emojiFont;
function loadEmojiFallback() {
  return emojiFont ??= loadFallbackFont('GalleryEmoji-Regular.ttf');
}

function typeText(text) {
  if (!text) return;
  if (/[\p{Script=Han}\u3000-\u303f\uff00-\uffef]/u.test(text)) cjkFont ??= loadFallbackFont('GallerySansSC-Regular.otf');
  if (/[\p{Extended_Pictographic}\p{Emoji_Presentation}]/u.test(text)) loadEmojiFallback();
  host.TypeText(text);
}

async function loadFallbackFont(asset) {
  try {
    const response = await fetch(new URL(`./fonts/${asset}`, import.meta.url));
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    if (!host.AddFont(new Uint8Array(await response.arrayBuffer()))) throw new Error('invalid font');
    return true;
  } catch (error) {
    console.warn(`Font fallback ${asset} was not installed:`, error);
    return false;
  }
}

function modifiers(event) {
  return (event.shiftKey ? 1 : 0) | (event.ctrlKey ? 2 : 0)
    | (event.altKey ? 4 : 0) | (event.metaKey ? 8 : 0);
}

function buttonCode(button) {
  return button === 2 ? 1 : button === 1 ? 2 : 0;
}

function resize() {
  if (!host) return;
  const rect = canvas.getBoundingClientRect();
  const width = Math.max(1, Math.floor(rect.width));
  const height = Math.max(1, Math.floor(rect.height));
  const scale = Math.max(1, window.devicePixelRatio || 1);
  // Rounded, matching CUI's own width_px = vw * scale + 0.5f, so the canvas and the frame
  // CUI lays out agree at fractional display scales such as 1.5.
  const pixelWidth = Math.max(1, Math.round(width * scale));
  const pixelHeight = Math.max(1, Math.round(height * scale));
  if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
    canvas.width = pixelWidth;
    canvas.height = pixelHeight;
    imageData = undefined;
  }
  host.Resize(width, height, scale);
  placeIme();   // the canvas moved or resized under the caret
}

function present(pointer, width, height, x, y, dirtyWidth, dirtyHeight) {
  const module = globalThis.getDotnetRuntime(0).Module;
  const heap = module.HEAPU8;
  if (!imageData || imagePointer !== pointer || imageWidth !== width || imageHeight !== height
      || imageMemory !== heap.buffer) {
    const pixels = new Uint8ClampedArray(heap.buffer, pointer, width * height * 4);
    imageData = new ImageData(pixels, width, height);
    imagePointer = pointer;
    imageWidth = width;
    imageHeight = height;
    imageMemory = heap.buffer;
  }

  // ImageData directly views the long-lived pinned managed framebuffer; putImageData
  // copies only the damage rectangle into the canvas backing store.
  context.putImageData(imageData, 0, 0, x, y, dirtyWidth, dirtyHeight);
}

function presentGpu(dd) {
  if (!gpu) return;
  if (query.has('debug')) window.__cuiDD = dd;
  gpu.draw(dd, canvas);
}

function animationFrame(timestamp) {
  if (!host) return;
  const started = performance.now();
  host.Frame(timestamp);
  if (measureFrames) {
    frameCosts.push(performance.now() - started);
    if (previousFrameTimestamp !== 0) frameIntervals.push(timestamp - previousFrameTimestamp);
    previousFrameTimestamp = timestamp;
    if (frameCosts.length === 180) {
      const costs = [...frameCosts].sort((a, b) => a - b);
      const intervals = [...frameIntervals].sort((a, b) => a - b);
      const mean = frameCosts.reduce((sum, value) => sum + value, 0) / frameCosts.length;
      const p50 = values => values[Math.floor(values.length * 0.50)];
      const p95 = values => values[Math.ceil(values.length * 0.95) - 1];
      console.info(`BROWSER-FRAME-PERF n=${costs.length} mean=${mean.toFixed(2)}ms `
        + `cost-p50=${p50(costs).toFixed(2)}ms cost-p95=${p95(costs).toFixed(2)}ms `
        + `raf-p50=${p50(intervals).toFixed(2)}ms raf-p95=${p95(intervals).toFixed(2)}ms`);
      frameCosts.length = 0;
      frameIntervals.length = 0;
    }
  }
  requestAnimationFrame(animationFrame);
}

function readRegion(x, y, width, height) {
  const scale = canvas.width / canvas.getBoundingClientRect().width;
  const left = Math.round(x * scale);
  const top = Math.round(y * scale);
  const w = Math.round(width * scale);
  const h = Math.round(height * scale);
  if (context) return context.getImageData(left, top, w, h).data;
  const gl = gpu.gl;
  const pixels = new Uint8Array(w * h * 4);
  gl.readPixels(left, canvas.height - top - h, w, h, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
  return pixels;
}

// Check the text ink inside the left end of the field, away from its border and caret.
function hasTextInk(x, y, height) {
  const pixels = readRegion(x + 8, y + 7, 18, height - 14);
  let ink = 0;
  for (let i = 0; i < pixels.length; i += 4) {
    if (pixels[i] < 160 && pixels[i + 1] < 160 && pixels[i + 2] < 160) ink++;
  }
  return ink > 10;
}

async function runSmoke() {
  const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const key = (type, code, options) => keyboardSink.dispatchEvent(new KeyboardEvent(type, { bubbles: true, code, ...options }));
  const input = (inputType, data, isComposing = false) => keyboardSink.dispatchEvent(new InputEvent('beforeinput', {
    bubbles: true, cancelable: true, inputType, data, isComposing,
  }));
  const selectAll = async () => {
    key('keydown', 'ControlLeft', { ctrlKey: true });
    key('keydown', 'KeyA', { ctrlKey: true });
    await frame();
    key('keyup', 'KeyA', { ctrlKey: true });
    key('keyup', 'ControlLeft');
  };
  try {
    await frame();
    const [x, y, width, height] = host.SmokeBounds('name');
    check(width > 0 && height > 0, 'name field has no layout bounds');
    const rect = canvas.getBoundingClientRect();
    const pointer = { bubbles: true, pointerId: 1, pointerType: 'mouse', button: 0,
      clientX: rect.left + x + width / 2, clientY: rect.top + y + height / 2 };
    canvas.dispatchEvent(new PointerEvent('pointerdown', { ...pointer, buttons: 1 }));
    await frame();
    canvas.dispatchEvent(new PointerEvent('pointerup', pointer));
    await frame();
    const anchor = keyboardSink.getBoundingClientRect();
    check(document.activeElement === keyboardSink && anchor.left >= rect.left + x
      && anchor.left <= rect.left + x + width && anchor.top >= rect.top + y
      && anchor.top < rect.top + y + height, `IME proxy is not anchored inside the name field (${anchor.left}, ${anchor.top})`);

    await selectAll();
    input('insertText', 'Browser Input');
    await frame();
    check(host.SmokeInputValue() === 'Browser Input', 'ordinary input did not replace the field value');

    await selectAll();
    keyboardSink.dispatchEvent(new CompositionEvent('compositionstart'));
    input('insertCompositionText', 'zhong', true);
    key('keydown', 'Backspace', { isComposing: true });
    await frame();
    check(host.SmokeInputValue() === 'Browser Input', 'IME preedit or its key leaked into the field');
    keyboardSink.dispatchEvent(new CompositionEvent('compositionend', { data: '中文' }));
    check(await cjkFont, 'Chinese font fallback did not load');
    await frame();
    check(host.SmokeInputValue() === '中文', 'IME commit was lost or duplicated');
    check(hasTextInk(x, y, height), 'the committed Chinese text did not paint');

    await selectAll();
    input('insertText', '🙂');
    check(await emojiFont, 'emoji text fallback did not load');
    await frame();
    check(host.SmokeInputValue() === '🙂', 'the text field did not retain the emoji');
    check(hasTextInk(x, y, height), 'the emoji in the text field did not paint');

    // Require warm-colored pixels in the picker itself. Counting raster callbacks
    // would also pass if the browser returned tofu or the renderer dropped the texture.
    host.SetPage(10);
    host.PointerMove(rect.width - 8, rect.height / 2); // scroll the page, not the terminal or the log
    await frame();
    let colorPixels = 0;
    for (let i = 0; i < 18 && colorPixels < 20; i++) {
      const [left, top, w, h] = host.SmokeBounds('emoji');
      const topVisible = Math.max(0, top);
      const visibleHeight = Math.min(rect.height, top + h) - topVisible;
      if (w > 0 && visibleHeight > 20) {
        const pixels = readRegion(left, topVisible, w, visibleHeight);
        colorPixels = 0;
        for (let j = 0; j < pixels.length; j += 4) {
          const r = pixels[j], g = pixels[j + 1], b = pixels[j + 2];
          if (r > 160 && g > 100 && r > b + 70 && g > b + 45) colorPixels++;
        }
      }
      if (colorPixels < 20) {
        host.Scroll(-6, 0);
        await frame();
      }
    }
    check(colorPixels >= 20, 'the Tools page picker did not display color emoji');
    status.textContent = 'BROWSER-SMOKE-OK: text input, IME anchor and commit, font fallback, and emoji';
  } catch (error) {
    status.style.display = 'grid';
    status.textContent = `BROWSER-SMOKE-FAIL: ${error.message}`;
    console.error(error);
  }
}

function preventBrowserNavigation(event) {
  if (event.code === 'Tab' || event.code.startsWith('Arrow') || event.code === 'PageUp'
      || event.code === 'PageDown' || event.code === 'Home' || event.code === 'End'
      || ((event.ctrlKey || event.metaKey) && ['KeyJ', 'KeyK'].includes(event.code))) {
    event.preventDefault();
  }
}

async function start() {
  try {
    const dotnetRuntime = await dotnet.create();
    dotnetRuntime.setModuleImports('main.js', {
      canvas: { present, presentGpu },
      ime: { place: setCaret },
      textures,
      emoji: { draw: drawEmoji },
    });
    await dotnetRuntime.runMain();
    const exports = await dotnetRuntime.getAssemblyExports(dotnetRuntime.getConfig().mainAssemblyName);
    host = exports.CuiSharp.Gallery.Browser.BrowserHost;

    // Renderer choice: WebGL2 by default, software with ?renderer=software or where WebGL2
    // is unavailable. The surface has to exist before the first frame, so the choice is made
    // here. Under --disable-gpu the first webgl2 request can race the GPU process coming up
    // and return null; the retry covers that and the later attempt succeeds. Ask the real
    // canvas directly - probing a scratch one would spend the one attempt the retry exists
    // to repeat, and a failed request leaves the canvas free to take a 2d context.
    for (let attempt = 0; wantGpu && !gpu && attempt < 12; attempt++) {
      try {
        gpu = new GLRenderer(canvas, keepBuffer);
      } catch (error) {
        gpu = null;
        if (attempt === 11) console.error('WebGL2 renderer failed to initialize:', error);
        else await new Promise(resolve => setTimeout(resolve, 250));
      }
    }
    host.Initialize(!!gpu, smoke);
    if (!gpu) {
      context = canvas.getContext('2d', { alpha: false });
      if (!context) throw new Error('neither WebGL2 nor a 2d context is available');
    }
    // What the smoke test reads pixels with. It must not call getContext itself:
    // on a canvas without a context that would create a 2d one and lock the
    // canvas out of webgl2.
    document.documentElement.dataset.renderer = gpu ? 'webgl2' : '2d';
    if (query.has('debug')) {
      window.__cui = {
        get gpu() { return gpu; },
        textures,
        get host() { return host; },
      };
    }

    const requestedPage = smoke ? 2 : Number(query.get('page'));
    if (Number.isInteger(requestedPage)) host.SetPage(requestedPage);

    resize();
    new ResizeObserver(resize).observe(canvas);
    window.addEventListener('resize', resize, { passive: true });

    function movePointer(event) {
      const rect = canvas.getBoundingClientRect();
      host.PointerMove(event.clientX - rect.left, event.clientY - rect.top);
    }
    canvas.addEventListener('pointermove', movePointer);
    canvas.addEventListener('pointerdown', event => {
      if (event.button > 2) return;
      try { canvas.setPointerCapture(event.pointerId); } catch { /* synthetic smoke-test pointer */ }
      if (document.activeElement !== keyboardSink) canvas.focus({ preventScroll: true });
      movePointer(event);
      host.PointerDown(buttonCode(event.button));
      event.preventDefault();
    });
    canvas.addEventListener('pointerup', event => {
      if (event.button > 2) return;
      movePointer(event);
      host.PointerUp(buttonCode(event.button));
    });
    canvas.addEventListener('pointercancel', () => {
      host.PointerLeave();
      for (let button = 0; button < 3; button++) host.PointerUp(button);
    });
    canvas.addEventListener('pointerleave', event => {
      if (event.buttons === 0) host.PointerLeave();
    });
    canvas.addEventListener('contextmenu', event => event.preventDefault());
    canvas.addEventListener('wheel', event => {
      const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16
        : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? canvas.clientHeight : 1;
      host.Scroll(-event.deltaY * unit / 100, event.deltaX * unit / 100);
      event.preventDefault();
    }, { passive: false });

    const ownsKeyboard = element => element === canvas || element === keyboardSink;
    const held = new Set();
    function loseFocus() {
      held.clear();
      host.Focus(false);
    }
    for (const element of [canvas, keyboardSink]) {
      // Moving focus from the canvas to its editor is not a focus loss in CUI.
      element.addEventListener('focus', event => {
        if (!ownsKeyboard(event.relatedTarget)) host.Focus(true);
      });
      element.addEventListener('blur', event => {
        if (!ownsKeyboard(event.relatedTarget)) loseFocus();
      });
      element.addEventListener('keydown', event => {
        // Some IMEs send their first key before compositionstart with keyCode 229.
        if (composing || event.isComposing || event.keyCode === 229) return;
        held.add(event.code);
        host.KeyDown(event.code, modifiers(event));
        // Type-to-search controls can consume characters without owning a text caret.
        // Only the textarea gets beforeinput, so send printable canvas keys directly.
        if (element === canvas && [...event.key].length === 1
            && ((!event.ctrlKey && !event.metaKey && !event.altKey) || event.getModifierState('AltGraph'))) {
          typeText(event.key);
          event.preventDefault();
        }
        preventBrowserNavigation(event);
      });
      element.addEventListener('keyup', event => {
        if (held.delete(event.code)) host.KeyUp(event.code, modifiers(event));
      });
      element.addEventListener('paste', event => {
        if (composing) return;
        event.preventDefault();
        typeText(event.clipboardData?.getData('text/plain'));
      });
    }
    window.addEventListener('blur', loseFocus);
    window.addEventListener('focus', () => {
      if (ownsKeyboard(document.activeElement)) host.Focus(true);
      placeIme();
    });
    keyboardSink.addEventListener('compositionstart', () => {
      composing = true;
      keyboardSink.classList.add('composing');
    });
    keyboardSink.addEventListener('compositionend', event => {
      composing = false;
      keyboardSink.classList.remove('composing');
      keyboardSink.value = '';
      keyboardSink.style.width = '';
      if (caret && document.activeElement === keyboardSink) typeText(event.data);
      placeIme();
    });
    keyboardSink.addEventListener('input', () => {
      if (composing) {
        keyboardSink.style.width = '1px';
        keyboardSink.style.width = `${keyboardSink.scrollWidth + 2}px`;
      } else {
        keyboardSink.value = '';
      }
    });
    keyboardSink.addEventListener('beforeinput', event => {
      // Preedit belongs to the DOM. Only compositionend sends the committed text to CUI.
      if (composing || event.isComposing) return;
      if (event.inputType === 'insertText' || event.inputType === 'insertReplacementText') {
        typeText(event.data);
        event.preventDefault();
      } else if (event.inputType.startsWith('delete') || event.inputType === 'insertLineBreak') {
        event.preventDefault();
      }
    });
    window.addEventListener('pagehide', () => host?.Shutdown(), { once: true });

    if (smoke) status.style.display = 'none';
    else status.remove();
    requestAnimationFrame(animationFrame);
    if (smoke) void runSmoke();
  } catch (error) {
    status.textContent = `CuiSharp browser startup failed: ${error}`;
    console.error(error);
  }
}

await start();
