/*
 * Hydra3D — lazy-loaded Three.js bottle for the configurator preview.
 * Procedural lathe/tube geometry only (no model assets); proportions come from
 * HydraBottle.GEOM so the 3D bottle matches the SVG renders (1 world unit =
 * 100 viewBox units). The SVG preview underneath remains the loading
 * placeholder and permanent fallback — any failure here leaves it untouched.
 */
import * as THREE from '/js/vendor/three.module.min.js';

const REDUCED_MOTION = window.matchMedia &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches;

const G = () => window.HydraBottle.GEOM;
const U = 1 / 100; // viewBox units -> world units

const R_BODY = 48 * U * 2 / 2;   // 0.48
const R_NARROW = 29 * U;
const CHAMFER = 0.05;

let renderer, scene, camera, pivot, shadowMesh;
let lidGroup, midGroup, baseGroup, threadGroups = [];
let mats = null;
let running = false, visible = true, pageVisible = true;
let lastTypes = { lid: null, mid: null, base: null };
let lastConfig = null;
let idleAt = 0, dragging = false, spinVel = 0;
let containerEl = null;
let lastTotal = 3.4;

/* ---------- materials ---------- */

function makeMats() {
  const phys = (color) => new THREE.MeshPhysicalMaterial({
    color, metalness: 0.25, roughness: 0.42, clearcoat: 0.5, clearcoatRoughness: 0.3,
    envMapIntensity: 0.75
  });
  return {
    lid: phys('#3a4550'), lidAcc: phys('#4a5f6e'),
    mid: phys('#3a4550'), midAcc: phys('#4a5f6e'),
    base: phys('#3a4550'), baseAcc: phys('#4a5f6e'),
    adapter: phys('#333D45'),
    thread: new THREE.MeshStandardMaterial({ color: '#2a2f36', metalness: 0.5, roughness: 0.55 }),
    heat: new THREE.MeshStandardMaterial({
      color: '#E2483D', emissive: '#FF7A45', emissiveIntensity: 1.1, roughness: 0.4
    }),
    led: new THREE.MeshStandardMaterial({ color: '#FFD9B0', emissive: '#FFB27A', emissiveIntensity: 1.5 })
  };
}

function sectionColor(c) {
  const col = new THREE.Color(c.main[0]);
  return col.lerp(new THREE.Color(c.main[1]), 0.45);
}

/* ---------- geometry helpers ---------- */

const v2 = (x, y) => new THREE.Vector2(x, y);
const v3 = (x, y, z) => new THREE.Vector3(x, y, z);

function lathe(points, mat) {
  const geo = new THREE.LatheGeometry(points, 56);
  geo.computeVertexNormals();
  return new THREE.Mesh(geo, mat);
}

function cylProfile(r, h) {
  const c = Math.min(CHAMFER, h / 3);
  return [v2(0.02, 0), v2(r - c, 0), v2(r, c), v2(r, h - c), v2(r - c, h), v2(0.02, h)];
}

function tube(curve, radius, mat) {
  return new THREE.Mesh(new THREE.TubeGeometry(curve, 32, radius, 14, false), mat);
}

function disposeGroup(group) {
  if (!group) return;
  group.traverse((o) => { if (o.geometry) o.geometry.dispose(); });
  pivot.remove(group);
}

/* ---------- module builders (bottom of module at local y = 0) ---------- */

function buildLid(type) {
  const g = new THREE.Group();
  const h = G().LID_H * U;
  const r = R_BODY;
  const dome = [
    v2(0.02, 0), v2(r - 0.05, 0), v2(r, 0.05),
    v2(r, h * 0.55), v2(r * 0.94, h * 0.82), v2(r * 0.74, h - 0.015), v2(0.02, h)
  ];
  g.add(lathe(dome, mats.lid));

  if (type === 'handle') {
    const arc = new THREE.QuadraticBezierCurve3(
      v3(-0.26, h - 0.04, 0), v3(0, h + 0.44, 0), v3(0.26, h - 0.04, 0));
    g.add(tube(arc, 0.048, mats.lidAcc));
    [-0.26, 0.26].forEach((x) => {
      const knob = new THREE.Mesh(new THREE.SphereGeometry(0.06, 18, 14), mats.lidAcc);
      knob.position.set(x, h - 0.04, 0);
      g.add(knob);
    });
  } else if (type === 'straw') {
    const spout = new THREE.Mesh(new THREE.CylinderGeometry(0.075, 0.09, 0.36, 22), mats.lidAcc);
    spout.rotation.z = 0.22;
    spout.position.set(-0.05, h + 0.13, 0);
    g.add(spout);
    const btn = new THREE.Mesh(new THREE.CylinderGeometry(0.075, 0.075, 0.10, 20), mats.lidAcc);
    btn.rotation.z = Math.PI / 2;
    btn.position.set(r - 0.01, h * 0.5, 0);
    g.add(btn);
  } else {
    const knob = new THREE.Mesh(new THREE.CylinderGeometry(0.13, 0.145, 0.10, 26), mats.lidAcc);
    knob.position.set(0, h + 0.045, 0);
    g.add(knob);
  }
  return g;
}

function buildMid(type) {
  const g = new THREE.Group();
  const h = (G().MID_H[type] || 150) * U;
  g.add(lathe(cylProfile(R_BODY, h), mats.mid));
  if (type === 'handle') {
    const d = new THREE.QuadraticBezierCurve3(
      v3(R_BODY - 0.03, h * 0.74, 0), v3(R_BODY + 0.40, h * 0.5, 0), v3(R_BODY - 0.03, h * 0.26, 0));
    g.add(tube(d, 0.05, mats.midAcc));
  }
  return g;
}

function buildBase(type) {
  const g = new THREE.Group();
  const bH = (G().BASE_H[type] || 88) * U;
  if (type === 'narrow') {
    const aH = G().ADAPTER_H * U;
    g.add(lathe(cylProfile(R_NARROW, bH), mats.base));
    const adapter = lathe(
      [v2(R_NARROW - 0.02, 0), v2(R_NARROW, 0.015), v2(R_BODY, aH - 0.015), v2(R_BODY - 0.02, aH)],
      mats.adapter);
    adapter.position.y = bH;
    g.add(adapter);
    g.userData.h = bH + aH;
  } else {
    g.add(lathe(cylProfile(R_BODY, bH), mats.base));
    g.userData.h = bH;
    if (type === 'heated') {
      const ring = new THREE.Mesh(new THREE.TorusGeometry(R_BODY - 0.015, 0.038, 14, 60), mats.heat);
      ring.rotation.x = Math.PI / 2;
      ring.position.y = 0.14;
      g.add(ring);
      const led = new THREE.Mesh(new THREE.SphereGeometry(0.024, 12, 10), mats.led);
      led.position.set(R_BODY - 0.04, bH - 0.1, 0.12);
      g.add(led);
    }
  }
  return g;
}

function buildThread(y) {
  const g = new THREE.Group();
  const tH = G().THREAD_H * U;
  for (let i = 0; i < 3; i++) {
    const t = new THREE.Mesh(new THREE.TorusGeometry(0.405, 0.017, 10, 48), mats.thread);
    t.rotation.x = Math.PI / 2;
    t.position.y = tH * (0.22 + i * 0.3);
    g.add(t);
  }
  g.position.y = y;
  return g;
}

/* ---------- environment + shadow ---------- */

function createEnvironment() {
  const envScene = new THREE.Scene();
  envScene.background = new THREE.Color(0x8f897d);
  const panel = (w, h, x, y, z, rx, ry, color, intensity) => {
    const m = new THREE.Mesh(
      new THREE.PlaneGeometry(w, h),
      new THREE.MeshBasicMaterial({ color: new THREE.Color(color).multiplyScalar(intensity), side: THREE.DoubleSide }));
    m.position.set(x, y, z);
    m.rotation.set(rx, ry, 0);
    envScene.add(m);
  };
  panel(8, 6, -5, 2, 1, 0, Math.PI / 2, 0xffffff, 1.15);  // key, left
  panel(6, 4, 5, 1, -1, 0, -Math.PI / 2, 0xffe9d2, 0.7);  // warm fill, right
  panel(10, 5, 0, 5, 0, Math.PI / 2, 0, 0xffffff, 0.9);   // top strip
  panel(8, 3, 0, 1, -5, 0, 0, 0xdfe8e2, 0.5);             // back bounce
  const pmrem = new THREE.PMREMGenerator(renderer);
  const tex = pmrem.fromScene(envScene, 0, 0.1, 100).texture;
  pmrem.dispose();
  return tex;
}

function makeShadow() {
  const cv = document.createElement('canvas');
  cv.width = cv.height = 128;
  const ctx = cv.getContext('2d');
  const grad = ctx.createRadialGradient(64, 64, 6, 64, 64, 62);
  grad.addColorStop(0, 'rgba(33,30,26,0.42)');
  grad.addColorStop(1, 'rgba(33,30,26,0)');
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, 128, 128);
  const tex = new THREE.CanvasTexture(cv);
  const mesh = new THREE.Mesh(
    new THREE.PlaneGeometry(2.3, 1.15),
    new THREE.MeshBasicMaterial({ map: tex, transparent: true, depthWrite: false }));
  mesh.rotation.x = -Math.PI / 2;
  return mesh;
}

/* ---------- layout ---------- */

function restack(config) {
  const tH = G().THREAD_H * U;
  const baseH = baseGroup.userData.h;
  const midH = (G().MID_H[config.mid] || 150) * U;
  const lidH = G().LID_H * U;
  const total = baseH + tH + midH + tH + lidH;

  threadGroups.forEach(disposeGroup);
  threadGroups = [buildThread(baseH), buildThread(baseH + tH + midH)];
  threadGroups.forEach((t) => pivot.add(t));

  baseGroup.position.y = 0;
  midGroup.position.y = baseH + tH;
  lidGroup.position.y = baseH + tH + midH + tH;

  // center the stack on the pivot origin so rotation feels natural
  [baseGroup, midGroup, lidGroup, ...threadGroups].forEach((g) => { g.position.y -= total / 2; });
  shadowMesh.position.y = -total / 2 - 0.005;

  lastTotal = total;
  fitCamera();
}

/* fit the whole stack (plus arch headroom / handle width) in BOTH fov axes */
function fitCamera() {
  const halfH = (lastTotal + 0.6) / 2;
  const halfW = 0.78; // body + side handle/button + margin
  const t = Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));
  const dist = Math.max(halfH / t, halfW / (t * camera.aspect), 3.2) * 1.1;
  camera.position.set(0, lastTotal * 0.06, dist);
  camera.lookAt(0, 0, 0);
}

/* ---------- public api ---------- */

export function update(config) {
  if (!scene || !config) return;
  lastConfig = config;
  const HB = window.HydraBottle;
  const lc = HB.resolveColors(config.lidColor);
  const mc = HB.resolveColors(config.midColor);
  const bc = HB.resolveColors(config.baseColor);

  let restacked = false;
  if (config.lid !== lastTypes.lid) {
    disposeGroup(lidGroup);
    lidGroup = buildLid(config.lid);
    pivot.add(lidGroup);
  }
  if (config.mid !== lastTypes.mid) {
    disposeGroup(midGroup);
    midGroup = buildMid(config.mid);
    pivot.add(midGroup);
    restacked = true;
  }
  if (config.base !== lastTypes.base) {
    disposeGroup(baseGroup);
    baseGroup = buildBase(config.base);
    pivot.add(baseGroup);
    restacked = true;
  }
  if (restacked || config.lid !== lastTypes.lid) restack(config);
  lastTypes = { lid: config.lid, mid: config.mid, base: config.base };

  mats.lid.color.copy(sectionColor(lc));
  mats.lidAcc.color.set(lc.accent);
  mats.mid.color.copy(sectionColor(mc));
  mats.midAcc.color.set(mc.accent);
  mats.base.color.copy(sectionColor(bc));
  mats.baseAcc.color.set(bc.accent);
  mats.adapter.color.set(bc.adapter);
  mats.thread.color.set(new THREE.Color(mc.main[1]).multiplyScalar(0.55));
}

export function init(container) {
  try {
    containerEl = container;
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setSize(container.clientWidth, container.clientHeight);
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.05;
    renderer.domElement.style.touchAction = 'pan-y';
    container.appendChild(renderer.domElement);

    scene = new THREE.Scene();
    camera = new THREE.PerspectiveCamera(30, container.clientWidth / container.clientHeight, 0.1, 50);

    scene.environment = createEnvironment();
    scene.add(new THREE.HemisphereLight(0xfaf6ef, 0x1f3d33, 0.35));
    const key = new THREE.DirectionalLight(0xffffff, 0.9);
    key.position.set(-3, 4, 3);
    scene.add(key);
    const rim = new THREE.DirectionalLight(0xffe9d2, 0.4);
    rim.position.set(3, 2, -3);
    scene.add(rim);

    pivot = new THREE.Group();
    scene.add(pivot);
    shadowMesh = makeShadow();
    scene.add(shadowMesh);
    mats = makeMats();

    /* pointer drag rotation with inertia */
    let px = 0, py = 0;
    const el = renderer.domElement;
    el.addEventListener('pointerdown', (e) => {
      dragging = true;
      px = e.clientX; py = e.clientY;
      spinVel = 0;
      el.setPointerCapture(e.pointerId);
    });
    el.addEventListener('pointermove', (e) => {
      if (!dragging) return;
      const dx = e.clientX - px, dy = e.clientY - py;
      px = e.clientX; py = e.clientY;
      pivot.rotation.y += dx * 0.009;
      pivot.rotation.x = Math.max(-0.3, Math.min(0.3, pivot.rotation.x + dy * 0.004));
      spinVel = dx * 0.009;
      idleAt = performance.now();
    });
    const release = () => { dragging = false; idleAt = performance.now(); };
    el.addEventListener('pointerup', release);
    el.addEventListener('pointercancel', release);

    /* pause when configurator is off-screen or tab hidden */
    new IntersectionObserver((entries) => {
      visible = entries[0].isIntersecting;
    }, { threshold: 0 }).observe(container);
    document.addEventListener('visibilitychange', () => {
      pageVisible = document.visibilityState === 'visible';
    });

    new ResizeObserver(() => {
      if (!containerEl.clientWidth) return;
      renderer.setSize(containerEl.clientWidth, containerEl.clientHeight);
      camera.aspect = containerEl.clientWidth / containerEl.clientHeight;
      camera.updateProjectionMatrix();
      fitCamera();
    }).observe(container);

    idleAt = performance.now();
    running = true;
    let lastT = performance.now();
    const loop = (t) => {
      if (!running) return;
      requestAnimationFrame(loop);
      if (!visible || !pageVisible) return;
      const dt = Math.min(0.05, (t - lastT) / 1000);
      lastT = t;
      if (!dragging) {
        pivot.rotation.y += spinVel;
        spinVel *= 0.94;
        if (!REDUCED_MOTION && t - idleAt > 3000) {
          pivot.rotation.y += 0.3 * dt;
          pivot.rotation.x *= 0.98;
        }
      }
      if (!REDUCED_MOTION && mats && lastTypes.base === 'heated') {
        mats.heat.emissiveIntensity = 1.1 + Math.sin(t / 420) * 0.45;
      }
      renderer.render(scene, camera);
    };
    requestAnimationFrame(loop);
    return true;
  } catch (err) {
    console.warn('Hydra3D init failed — keeping SVG preview.', err);
    try { if (renderer) { renderer.dispose(); renderer.domElement.remove(); } } catch (e) { /* noop */ }
    scene = null;
    return false;
  }
}

window.Hydra3D = { init, update };
