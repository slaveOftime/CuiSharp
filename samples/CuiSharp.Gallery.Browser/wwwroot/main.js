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
  constructor(canvas, smoke) {
    const gl = canvas.getContext('webgl2', {
      alpha: false, antialias: false, depth: false, stencil: false,
      premultipliedAlpha: true, preserveDrawingBuffer: keepBuffer,
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
    gl.attachShader(p, sh(gl.VERTEX_SHADER, VS));
    gl.attachShader(p, sh(gl.FRAGMENT_SHADER, FS));
    ['a_pos', 'a_uv', 'a_local', 'a_half', 'a_radii', 'a_params', 'a_color', 'a_bcolor'].forEach((n, i) => gl.bindAttribLocation(p, i, n));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error('CUI program: ' + gl.getProgramInfoLog(p));
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
    if (fmt === 1) gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, w, h, 0, gl.RED, gl.UNSIGNED_BYTE, heapBytes(px, w * h));
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, heapBytes(px, w * h * 4));
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
    gl.texSubImage2D(gl.TEXTURE_2D, 0, x, y, w, h, e.fmt === 1 ? gl.RED : gl.RGBA, gl.UNSIGNED_BYTE,
      heapBytes(px, stride * h));
    gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
  }
  destroyTexture(id) {
    const e = this.textures.get(id);
    if (e) { this.gl.deleteTexture(e.t); this.textures.delete(id); }
  }

  // draws the cui_draw_data at `dd` (a wasm heap pointer)
  draw(dd) {
    const gl = this.gl, buf = heapBuffer();
    const i32 = new Int32Array(buf), u32 = new Uint32Array(buf), f32 = new Float32Array(buf);
    const verts = u32[dd >> 2], vcount = i32[(dd + 4) >> 2];
    const idx = u32[(dd + 8) >> 2], icount = i32[(dd + 12) >> 2];
    const cmds = u32[(dd + 16) >> 2], ccount = i32[(dd + 20) >> 2];
    const W = i32[(dd + 24) >> 2], H = i32[(dd + 28) >> 2];
    const cc = u32[(dd + 32) >> 2];       // r | g<<8 | b<<16, straight from cui_color
    const atlas = u32[(dd + 36) >> 2];
    if (W !== canvas.width || H !== canvas.height) throw new Error(`draw data ${W}x${H} != canvas ${canvas.width}x${canvas.height}`);
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
let smokeState = 0;
let selectedNameHash = 0;
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
  const pixelWidth = Math.max(1, Math.floor(width * scale));
  const pixelHeight = Math.max(1, Math.floor(height * scale));
  if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
    canvas.width = pixelWidth;
    canvas.height = pixelHeight;
    imageData = undefined;
  }
  host.Resize(width, height, scale);
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

function presentGpu(dd, width, height) {
  if (!gpu) return;
  if (query.has('debug')) window.__cuiDD = dd;
  gpu.draw(dd);
}

function animationFrame(timestamp) {
  if (!host) return;
  const started = performance.now();
  host.Frame(timestamp);
  if (smoke && smokeState === 0) startInputSmoke();
  else if (smoke && smokeState === 2) smokeState = 3;
  else if (smoke && smokeState === 4) {
    selectedNameHash = hashNameField();
    smokeState = 5;
  } else if (smoke && smokeState === 6) {
    const typedNameHash = hashNameField();
    smokeState = 5;
    status.style.display = 'grid';
    const passed = typedNameHash !== selectedNameHash;
    smokeState = 7;
    status.style.opacity = passed ? '0' : '1';
    status.textContent = passed
      ? 'BROWSER-SMOKE-OK: canvas rendered and text input changed'
      : 'BROWSER-SMOKE-FAIL: text input did not change';
    if (typedNameHash === selectedNameHash) console.error(status.textContent);
  }
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

// A cheap fingerprint of a canvas region, used by the smoke test to prove text
// input changed what the frame shows. Works over both renderers.
function hashNameField() {
  const rect = canvas.getBoundingClientRect();
  const scale = canvas.width / rect.width;
  const x = Math.round(rect.width * 0.28 * scale);
  const y = Math.round(220 * scale);
  const width = Math.max(1, Math.round(rect.width * 0.31 * scale));
  const height = Math.max(1, Math.round(50 * scale));
  const pixels = readRegion(x, y, width, height);
  let hash = 2166136261;
  for (let i = 0; i < pixels.length; i++) hash = Math.imul(hash ^ pixels[i], 16777619) >>> 0;
  return hash;
}

function readRegion(x, y, width, height) {
  if (context) return context.getImageData(x, y, width, height).data;
  const gl = gpu.gl;
  const pixels = new Uint8Array(width * height * 4);
  gl.readPixels(x, canvas.height - y - height, width, height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
  return pixels;
}

function startInputSmoke() {
  const rect = canvas.getBoundingClientRect();
  const x = rect.left + rect.width * 0.45;
  const y = rect.top + 245;
  const pointer = { bubbles: true, pointerId: 1, pointerType: 'mouse', button: 0, clientX: x, clientY: y };
  canvas.dispatchEvent(new PointerEvent('pointermove', { ...pointer, buttons: 0 }));
  canvas.dispatchEvent(new PointerEvent('pointerdown', { ...pointer, buttons: 1 }));
  smokeState = 1;
  requestAnimationFrame(() => {
    // Let CUI see the pressed button in one full frame before releasing it.
    requestAnimationFrame(() => {
      canvas.dispatchEvent(new PointerEvent('pointerup', { ...pointer, buttons: 0 }));
      smokeState = 2;
      requestAnimationFrame(() => {
        window.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, code: 'ControlLeft', key: 'Control', ctrlKey: true }));
        window.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, code: 'KeyA', key: 'a', ctrlKey: true }));
        smokeState = 4;
        requestAnimationFrame(() => {
          window.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, code: 'KeyA', key: 'a', ctrlKey: true }));
          window.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, code: 'ControlLeft', key: 'Control' }));
          keyboardSink.dispatchEvent(new InputEvent('beforeinput', {
            bubbles: true, cancelable: true, inputType: 'insertText', data: 'Browser Input',
          }));
          smokeState = 6;
        });
      });
    });
  });
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
      textures,
    });
    await dotnetRuntime.runMain();
    const exports = await dotnetRuntime.getAssemblyExports(dotnetRuntime.getConfig().mainAssemblyName);
    host = exports.CuiSharp.Gallery.Browser.BrowserHost;

    // Renderer choice: WebGL2 by default, software with ?renderer=software or
    // where WebGL2 is missing. The surface has to exist before the first frame.
    // Under --disable-gpu (CI) the first webgl2 request can race the GPU
    // process coming up and return null; a short retry covers that, and the
    // later attempt does succeed.
    let supported = false;
    if (wantGpu) supported = !!document.createElement('canvas').getContext('webgl2');
    if (supported) {
      for (let attempt = 0; attempt < 12 && !gpu; attempt++) {
        try {
          gpu = new GLRenderer(canvas, smoke);
        } catch (error) {
          gpu = null;
          if (attempt === 11) console.error('WebGL2 renderer failed to initialize:', error);
          else await new Promise(resolve => setTimeout(resolve, 250));
        }
      }
    }
    await host.Initialize(!!gpu);
    if (!gpu) {
      context = canvas.getContext('2d', { alpha: false });
      if (!context) throw new Error('neither WebGL2 nor a 2d context is available');
    }
    // What the smoke test reads pixels with. It must not call getContext itself:
    // on a canvas without a context that would create a 2d one and lock the
    // canvas out of webgl2.
    document.documentElement.dataset.renderer = gpu ? 'webgl2' : '2d';
    if (query.has('debug')) window.__cui = { get gpu() { return gpu; }, textures, get host() { return host; } };

    const requestedPage = Number(new URLSearchParams(location.search).get('page'));
    if (Number.isInteger(requestedPage) && requestedPage >= 0 && requestedPage < 28)
      host.SetPage(requestedPage);

    resize();
    new ResizeObserver(resize).observe(canvas);
    window.addEventListener('resize', resize, { passive: true });

    canvas.addEventListener('pointermove', event => {
      const rect = canvas.getBoundingClientRect();
      host.PointerMove(event.clientX - rect.left, event.clientY - rect.top);
    });
    canvas.addEventListener('pointerdown', event => {
      try { canvas.setPointerCapture(event.pointerId); } catch { /* synthetic smoke-test pointer */ }
      keyboardSink.focus({ preventScroll: true });
      host.PointerDown(buttonCode(event.button));
      event.preventDefault();
    });
    canvas.addEventListener('pointerup', event => host.PointerUp(buttonCode(event.button)));
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

    keyboardSink.addEventListener('focus', () => host.Focus(true));
    keyboardSink.addEventListener('blur', () => host.Focus(false));
    window.addEventListener('blur', () => host.Focus(false));
    window.addEventListener('keydown', event => {
      host.KeyDown(event.code, modifiers(event));
      preventBrowserNavigation(event);
    });
    window.addEventListener('keyup', event => host.KeyUp(event.code, modifiers(event)));
    keyboardSink.addEventListener('beforeinput', event => {
      if (event.inputType === 'insertText' || event.inputType === 'insertReplacementText'
          || event.inputType === 'insertFromComposition') {
        if (event.data) host.TypeText(event.data);
        event.preventDefault();
      } else if (event.inputType.startsWith('delete') || event.inputType === 'insertLineBreak') {
        event.preventDefault();
      }
    });
    keyboardSink.addEventListener('paste', event => {
      event.preventDefault();
      const text = event.clipboardData?.getData('text/plain');
      if (text) host.TypeText(text);
    });
    window.addEventListener('pagehide', () => host?.Shutdown(), { once: true });

    if (smoke) status.style.display = 'none';
    else status.remove();
    requestAnimationFrame(animationFrame);
  } catch (error) {
    status.textContent = `CuiSharp browser startup failed: ${error}`;
    console.error(error);
  }
}

await start();
