import { dotnet } from './_framework/dotnet.js';

const canvas = document.querySelector('#surface');
const keyboardSink = document.querySelector('#keyboard-sink');
const status = document.querySelector('#status');
const context = canvas.getContext('2d', { alpha: false });
let host;
let imageData;
let imageMemory;
let imagePointer = -1;
let imageWidth = 0;
let imageHeight = 0;
const query = new URLSearchParams(location.search);
const measureFrames = query.has('perf');
const smoke = query.has('smoke');
let smokeState = 0;
let selectedNameHash = 0;
const frameCosts = [];
const frameIntervals = [];
let previousFrameTimestamp = 0;

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

function hashNameField() {
  const rect = canvas.getBoundingClientRect();
  const scale = canvas.width / rect.width;
  const x = Math.round(rect.width * 0.28 * scale);
  const y = Math.round(220 * scale);
  const width = Math.max(1, Math.round(rect.width * 0.31 * scale));
  const height = Math.max(1, Math.round(50 * scale));
  const pixels = context.getImageData(x, y, width, height).data;
  let hash = 2166136261;
  for (let i = 0; i < pixels.length; i++) hash = Math.imul(hash ^ pixels[i], 16777619) >>> 0;
  return hash;
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
    dotnetRuntime.setModuleImports('main.js', { canvas: { present } });
    await dotnetRuntime.runMain();
    const exports = await dotnetRuntime.getAssemblyExports(dotnetRuntime.getConfig().mainAssemblyName);
    host = exports.CuiSharp.Gallery.Browser.BrowserHost;
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
