import * as THREE from "three"

// A bespoke WebGL orb for ARI's voice mode, on the shared dark (code-mode) backdrop so it can bloom.
// Its own design in the spirit of a HUD orb: a white-hot glowing core, a particle shell, many concentric
// tilted rings + bands, and a bright equatorial scan-band. Additive blending for real glow; ARI's cerulean
// hue with the saturation turned up. Brightens + saturates further while ARI speaks (state) or on pulse.

export type OrbStateName = "idle" | "listening" | "thinking" | "speaking" | "interrupted"

interface Target {
    energy: number; rotation: number; particle: number; coreScale: number; bloom: number; ringSpread: number
}

const STATES: Record<OrbStateName, Target> = {
    idle:        { energy: 0.55, rotation: 0.45, particle: 0.60, coreScale: 1.00, bloom: 0.65, ringSpread: 1.00 },
    listening:   { energy: 0.75, rotation: 0.70, particle: 1.05, coreScale: 1.05, bloom: 0.90, ringSpread: 1.05 },
    thinking:    { energy: 0.85, rotation: 1.55, particle: 1.35, coreScale: 1.01, bloom: 1.05, ringSpread: 1.09 },
    speaking:    { energy: 1.00, rotation: 0.95, particle: 1.45, coreScale: 1.12, bloom: 1.40, ringSpread: 1.06 },
    interrupted: { energy: 0.30, rotation: 0.35, particle: 0.40, coreScale: 0.95, bloom: 0.35, ringSpread: 0.97 },
}

// ARI's cerulean hue, saturated. Additive over the dark backdrop => these read as bright glowing blues.
const COL = {
    hot:    0xeafcff, // near-white hot core centre
    bright: 0x5cdeff, // bright cyan
    cyan:   0x0fcfff, // vivid cyan
    cer:    0x00bceb, // saturated cerulean
    deep:   0x009fc7, // deeper cerulean
}

const TAU = Math.PI * 2

export interface OrbRenderer {
    setState: (state: OrbStateName) => void
    pulse: (amount?: number) => void
    resize: () => void
    setPaused: (paused: boolean) => void
    dispose: () => void
}

function lerp(a: number, b: number, t: number) { return a + (b - a) * t }
function seeded(n: number) { const v = Math.sin(n * 12.9898) * 43758.5453; return v - Math.floor(v) }

function spherePoint(i: number, count: number, radius: number): THREE.Vector3 {
    const offset = 2 / count
    const inc = Math.PI * (3 - Math.sqrt(5))
    const y = i * offset - 1 + offset / 2
    const r = Math.sqrt(Math.max(0, 1 - y * y))
    const phi = i * inc
    return new THREE.Vector3(Math.cos(phi) * r * radius, y * radius, Math.sin(phi) * r * radius)
}

const ZAXIS = new THREE.Vector3(0, 0, 1)

// Per-element fade as a hold-then-transition state machine: an element eases to a level, then STAYS IDLE
// there (visible or hidden) for 10–30s before easing to the next — so it isn't perpetually fading. Hidden
// holds run longer than visible ones to keep only a few showing at once. `fMin` is the hidden floor
// (0 = fully gone; anchors dim instead of disappearing). Returns fields merged into userData.
function fadeParams(seed: number, fMin?: number) {
    const min = fMin ?? (seeded(seed + 9) < 0.72 ? 0 : 0.24 + seeded(seed + 5) * 0.18)
    const startVisible = seeded(seed + 2) < 0.35
    return {
        fMin: min,
        fVal: startVisible ? 0.72 + seeded(seed + 7) * 0.28 : min,
        fVisible: startVisible,
        fFrom: 0, fTarget: 0, fT: 0, fDur: 0,
        fHolding: true,
        fHoldOffset: seeded(seed + 13) * 22000, // stagger the first transition so they desync
        fHoldUntil: 0,                           // initialised on first frame relative to now
    }
}

function haloTexture(color: THREE.Color): THREE.CanvasTexture {
    const size = 256
    const c = document.createElement("canvas"); c.width = c.height = size
    const ctx = c.getContext("2d")!
    const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2)
    const rgb = `${Math.round(color.r * 255)},${Math.round(color.g * 255)},${Math.round(color.b * 255)}`
    g.addColorStop(0, "rgba(255,255,255,0.9)")
    g.addColorStop(0.16, `rgba(${rgb},0.6)`)
    g.addColorStop(0.5, `rgba(${rgb},0.18)`)
    g.addColorStop(1, `rgba(${rgb},0)`)
    ctx.fillStyle = g; ctx.fillRect(0, 0, size, size)
    const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace
    return t
}

// A dashed arc over a partial span (never a full circle).
function dashedRing(radius: number, segments: number, seed: number, thetaStart: number, thetaSpan: number): THREE.BufferGeometry {
    const v: number[] = []
    for (let i = 0; i < segments; i++) {
        if (seeded(i * 2.17 + seed) < 0.32) continue
        const a0 = thetaStart + (i / segments) * thetaSpan
        const a1 = thetaStart + ((i + 0.62 + seeded(i + seed) * 0.2) / segments) * thetaSpan
        v.push(Math.cos(a0) * radius, Math.sin(a0) * radius, 0, Math.cos(a1) * radius, Math.sin(a1) * radius, 0)
    }
    const g = new THREE.BufferGeometry()
    g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3))
    return g
}

function easeInOut(t: number) { return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2 }

// A thick arc broken into irregular filled segments with gaps — the segmented "gauge" look. Flat in XY.
function segmentedArc(inner: number, outer: number, thetaStart: number, thetaLength: number, count: number, seed: number): THREE.BufferGeometry {
    const v: number[] = []
    const quad = (a: number[], b: number[], c: number[], d: number[]) => v.push(...a, ...b, ...c, ...a, ...c, ...d)
    for (let i = 0; i < count; i++) {
        if (seeded(i * 4.3 + seed) < 0.12) continue // occasional missing block → irregular
        const slot = thetaLength / count
        const a0 = thetaStart + i * slot + slot * 0.12
        const width = slot * (0.5 + seeded(i * 3.1 + seed) * 0.4) // irregular block widths
        const steps = 4
        for (let s = 0; s < steps; s++) {
            const t0 = a0 + width * (s / steps)
            const t1 = a0 + width * ((s + 1) / steps)
            quad(
                [Math.cos(t0) * inner, Math.sin(t0) * inner, 0],
                [Math.cos(t0) * outer, Math.sin(t0) * outer, 0],
                [Math.cos(t1) * outer, Math.sin(t1) * outer, 0],
                [Math.cos(t1) * inner, Math.sin(t1) * inner, 0],
            )
        }
    }
    const g = new THREE.BufferGeometry()
    g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3))
    return g
}

// A broken outline ring like the JARVIS HUD: an incomplete circle whose thickness steps between thin and
// thick sections (centred on `radius`), with thicker geometric bracket caps terminating each end.
function hudArc(radius: number, thetaStart: number, thetaLength: number, seed: number): THREE.BufferGeometry {
    const v: number[] = []
    const quad = (a: number[], b: number[], c: number[], d: number[]) => v.push(...a, ...b, ...c, ...a, ...c, ...d)
    const ribbon = (t0: number, t1: number, th: number) => {
        const inner = radius - th / 2, outer = radius + th / 2
        const steps = Math.max(2, Math.round((t1 - t0) / 0.12))
        for (let s = 0; s < steps; s++) {
            const u0 = t0 + (t1 - t0) * (s / steps), u1 = t0 + (t1 - t0) * ((s + 1) / steps)
            quad(
                [Math.cos(u0) * inner, Math.sin(u0) * inner, 0], [Math.cos(u0) * outer, Math.sin(u0) * outer, 0],
                [Math.cos(u1) * outer, Math.sin(u1) * outer, 0], [Math.cos(u1) * inner, Math.sin(u1) * inner, 0],
            )
        }
    }
    const K = 8
    for (let k = 0; k < K; k++) {
        const slot = thetaLength / K
        const a0 = thetaStart + k * slot
        const width = slot * (0.62 + seeded(k * 2.3 + seed) * 0.34) // leaves a small gap → broken
        const th = (k === 0 || k === K - 1) ? 0.09 : (seeded(k * 3.7 + seed) > 0.5 ? 0.062 : 0.024) // thin/thick, thick ends
        ribbon(a0, a0 + width, th)
    }
    // geometric bracket terminators at the two ends (short, thick radial blocks)
    ribbon(thetaStart - 0.01, thetaStart + 0.01, 0.15)
    ribbon(thetaStart + thetaLength - 0.01, thetaStart + thetaLength + 0.01, 0.15)
    const g = new THREE.BufferGeometry()
    g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3))
    return g
}

// Irregular radial tick marks over a partial span — varying lengths, some missing (never a full circle).
function tickRing(radius: number, count: number, seed: number, thetaStart: number, thetaSpan: number): THREE.BufferGeometry {
    const v: number[] = []
    for (let i = 0; i < count; i++) {
        if (seeded(i * 1.9 + seed) < 0.18) continue
        const a = thetaStart + (i / count) * thetaSpan
        const len = 0.025 + seeded(i * 1.7 + seed) * 0.075 // irregular tick length
        v.push(Math.cos(a) * radius, Math.sin(a) * radius, 0, Math.cos(a) * (radius + len), Math.sin(a) * (radius + len), 0)
    }
    const g = new THREE.BufferGeometry()
    g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3))
    return g
}

// Glowing core: additive fresnel body brightening soft->vivid with activation.
function coreMaterial(): THREE.ShaderMaterial {
    return new THREE.ShaderMaterial({
        transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
        uniforms: {
            uCool: { value: new THREE.Color(COL.cer) },
            uVivid: { value: new THREE.Color(COL.cyan) },
            uAct: { value: 0.4 }, uTime: { value: 0 },
        },
        vertexShader: `
            varying vec3 vN; varying vec3 vV;
            void main() {
                vec4 mv = modelViewMatrix * vec4(position, 1.0);
                vN = normalize(normalMatrix * normal);
                vV = normalize(-mv.xyz);
                gl_Position = projectionMatrix * mv;
            }`,
        fragmentShader: `
            uniform vec3 uCool; uniform vec3 uVivid; uniform float uAct; uniform float uTime;
            varying vec3 vN; varying vec3 vV;
            void main() {
                float facing = max(dot(normalize(vN), normalize(vV)), 0.0);
                float fres = pow(1.0 - facing, 2.2);
                float shim = 0.85 + 0.15 * sin(uTime * 2.8 + vN.y * 16.0 + vN.x * 8.0);
                vec3 col = mix(uCool, uVivid, uAct);
                // glowing shell: bright rim (fresnel) + soft centre fill, all additive
                float glow = (0.28 + facing * 0.4 + fres * (0.7 + uAct * 0.4)) * shim;
                gl_FragColor = vec4(col * glow, glow);
            }`,
    })
}

function particleMaterial(dpr: number): THREE.ShaderMaterial {
    return new THREE.ShaderMaterial({
        transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
        uniforms: {
            uCool: { value: new THREE.Color(COL.cer) },
            uHot: { value: new THREE.Color(COL.bright) },
            uAct: { value: 0.4 }, uTime: { value: 0 }, uEnergy: { value: 0.6 }, uPulse: { value: 0 }, uDpr: { value: dpr },
        },
        vertexShader: `
            attribute float aSeed; attribute float aScale; attribute float aBright;
            varying float vA; varying float vTw;
            uniform float uTime; uniform float uEnergy; uniform float uPulse; uniform float uDpr;
            void main() {
                vec3 p = position;
                float wob = sin(uTime * (0.25 + aSeed * 0.3) + aSeed * 30.0) * 0.03;
                p *= 1.0 + wob * uEnergy + uPulse * 0.05;
                vec4 mv = modelViewMatrix * vec4(p, 1.0);
                gl_Position = projectionMatrix * mv;
                float tw = 0.5 + 0.5 * sin(uTime * (1.4 + aSeed * 2.2) + aSeed * 60.0);
                vA = (0.34 + tw * 0.6) * (0.65 + uEnergy * 0.32 + uPulse * 0.32) * aBright;
                vTw = tw;
                gl_PointSize = uDpr * 2.85 * aScale * (1.0 + tw * 1.25 + uPulse * 0.6) * (3.0 / max(0.7, -mv.z));
            }`,
        fragmentShader: `
            varying float vA; varying float vTw; uniform vec3 uCool; uniform vec3 uHot; uniform float uAct;
            void main() {
                float d = length(gl_PointCoord.xy - 0.5);
                float a = smoothstep(0.5, 0.0, d) * vA;
                vec3 col = mix(uCool, uHot, clamp(uAct + vTw * 0.25, 0.0, 1.0));
                gl_FragColor = vec4(col * a, a);
            }`,
    })
}

// Which frequency band drives an element, by radius — inner reacts to lows, outer to highs.
// (band 0 = bass, reserved for the core; 1..4 = low→air across the shell)
function bandForRadius(r: number): number {
    if (r < 0.6) return 1
    if (r < 0.85) return 2
    if (r < 1.05) return 3
    return 4
}

// getAudio (optional) returns ORB_BANDS frequency levels 0..1 while Ari speaks; drives the equaliser glow.
export function createOrbRenderer(canvas: HTMLCanvasElement, getAudio?: () => number[]): OrbRenderer {
    const dpr = Math.max(1, Math.min(window.devicePixelRatio || 1, 2))
    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true, premultipliedAlpha: false })
    renderer.setPixelRatio(dpr)
    renderer.setClearColor(0x000000, 0)
    renderer.outputColorSpace = THREE.SRGBColorSpace

    const scene = new THREE.Scene()
    const camera = new THREE.PerspectiveCamera(35, 1, 0.1, 100)
    camera.position.set(0, 0, 5.0)

    const root = new THREE.Group()
    const ringGroup = new THREE.Group()
    const coreGroup = new THREE.Group()
    scene.add(root); root.add(coreGroup, ringGroup)

    // bloom halo
    const halo = new THREE.Sprite(new THREE.SpriteMaterial({
        map: haloTexture(new THREE.Color(COL.cer)),
        color: COL.cer, transparent: true, opacity: 0.7,
        blending: THREE.AdditiveBlending, depthWrite: false,
    }))
    halo.scale.setScalar(2.7); root.add(halo)

    // glowing core + white-hot inner point
    const coreMat = coreMaterial()
    const core = new THREE.Mesh(new THREE.IcosahedronGeometry(0.3, 4), coreMat)
    coreGroup.add(core)
    const innerMat = new THREE.MeshBasicMaterial({ color: COL.hot, transparent: true, opacity: 0.9, blending: THREE.AdditiveBlending, depthWrite: false })
    const innerCore = new THREE.Mesh(new THREE.SphereGeometry(0.085, 20, 12), innerMat)
    coreGroup.add(innerCore)

    // particle shell — per-dot size + brightness vary; the current look is the floor (scale/bright = 1),
    // with a weighted spread of larger, brighter dots above it so some really stand out.
    const COUNT = 640
    const pos = new Float32Array(COUNT * 3); const seeds = new Float32Array(COUNT)
    const scales = new Float32Array(COUNT); const brights = new Float32Array(COUNT)
    for (let i = 0; i < COUNT; i++) {
        const p = spherePoint(i, COUNT, 1.0 * (0.9 + seeded(i + 2.1) * 0.2))
        pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z
        seeds[i] = seeded(i + 41.2)
        scales[i] = 1 + Math.pow(seeded(i + 120.5), 1.6) * 2.1   // 1.0 … ~3.1, mostly small
        brights[i] = 1 + Math.pow(seeded(i + 205.9), 1.7) * 1.3  // 1.0 … ~2.3
    }
    const pGeo = new THREE.BufferGeometry()
    pGeo.setAttribute("position", new THREE.BufferAttribute(pos, 3))
    pGeo.setAttribute("aSeed", new THREE.BufferAttribute(seeds, 1))
    pGeo.setAttribute("aScale", new THREE.BufferAttribute(scales, 1))
    pGeo.setAttribute("aBright", new THREE.BufferAttribute(brights, 1))
    const pMat = particleMaterial(dpr)
    const particles = new THREE.Points(pGeo, pMat)
    root.add(particles)

    // radial rays emanating outward from the centre, varying length
    const RAYS = 30
    const rverts: number[] = []
    for (let i = 0; i < RAYS; i++) {
        const dir = spherePoint(i, RAYS, 1)
        const r0 = 0.1 + seeded(i + 301) * 0.06
        const r1 = 0.55 + seeded(i + 317) * 0.6
        rverts.push(dir.x * r0, dir.y * r0, dir.z * r0, dir.x * r1, dir.y * r1, dir.z * r1)
    }
    const rayGeo = new THREE.BufferGeometry()
    rayGeo.setAttribute("position", new THREE.Float32BufferAttribute(rverts, 3))
    const rayMat = new THREE.LineBasicMaterial({ color: COL.cer, transparent: true, opacity: 0.16, blending: THREE.AdditiveBlending, depthWrite: false })
    const rays = new THREE.LineSegments(rayGeo, rayMat)
    root.add(rays)

    const coolC = new THREE.Color(COL.cer), hotC = new THREE.Color(COL.cyan), briteC = new THREE.Color(COL.bright)

    // every element is a "disk": a fragmented (partial-arc) shape that spins on its own normal axis and
    // periodically eased-jumps at least 60°. No complete circles anywhere.
    const mkDisk = (seed: number, spin: number, base: number, cool: THREE.Color, hot: THREE.Color, fMin?: number) => ({
        isDisk: true, spin, base, cool, hot, seed,
        nextJump: 0, jumpActive: false, jumpT: 0, jumpDur: 0, jumpTotal: 0, jumpPrevEased: 0,
        ...fadeParams(seed, fMin),
    })

    // partial dashed arcs at graduated radii
    const ringCfg = [
        { r: 0.46, tx: 0.9, ty: -0.2, sp: 0.5, op: 0.55, seed: 2.4, span: TAU * 0.5 },
        { r: 0.62, tx: -0.55, ty: 0.35, sp: -0.44, op: 0.6, seed: 8.1, span: TAU * 0.42 },
        { r: 0.78, tx: 0.28, ty: 0.95, sp: 0.36, op: 0.52, seed: 14.7, span: TAU * 0.6 },
        { r: 0.95, tx: -0.2, ty: -0.72, sp: -0.3, op: 0.48, seed: 21.3, span: TAU * 0.38 },
        { r: 1.12, tx: 0.62, ty: 0.5, sp: 0.24, op: 0.42, seed: 27.9, span: TAU * 0.55 },
    ]
    for (const c of ringCfg) {
        const m = new THREE.LineBasicMaterial({ color: COL.cer, transparent: true, opacity: c.op, blending: THREE.AdditiveBlending, depthWrite: false })
        const ring = new THREE.LineSegments(dashedRing(c.r, 120, c.seed, 0, c.span), m)
        ring.rotation.x = c.tx; ring.rotation.y = c.ty; ring.rotation.z = seeded(c.seed) * TAU
        ring.userData = mkDisk(c.seed, c.sp, c.op, coolC, hotC)
        ring.userData.band = bandForRadius(c.r)
        ringGroup.add(ring)
    }

    // partial thin bands (arc tori)
    const bandCfg = [
        { r: 0.7, tx: 1.25, ty: 0.08, sp: 0.5, op: 0.5, fmin: 0.25, span: TAU * 0.55 },
        { r: 0.98, tx: 1.32, ty: 0.1, sp: 0.6, op: 0.7, fmin: 0.5, span: TAU * 0.68 }, // signature scan-band arc
        { r: 1.2, tx: 1.18, ty: -0.14, sp: -0.34, op: 0.4, fmin: 0.2, span: TAU * 0.5 },
    ]
    for (const c of bandCfg) {
        const m = new THREE.MeshBasicMaterial({ color: COL.bright, transparent: true, opacity: c.op, blending: THREE.AdditiveBlending, depthWrite: false })
        const band = new THREE.Mesh(new THREE.TorusGeometry(c.r, 0.0095, 8, 160, c.span), m)
        band.rotation.x = c.tx; band.rotation.y = c.ty; band.rotation.z = seeded(c.r * 13.7) * TAU
        band.userData = mkDisk(c.r * 20 + 5, c.sp, c.op, hotC, briteC, c.fmin)
        band.userData.band = bandForRadius(c.r)
        ringGroup.add(band)
    }

    // thick partial-arc "disks" (flat annulus segments) that spin on their own normal axis and periodically
    // snap-rotate a random amount. They fade in/out, so the composition is never the same twice.
    const diskCfg = [
        { ri: 0.5, ro: 0.66, ts: 0.3, tl: TAU * 0.42, tx: 0.85, ty: 0.25, sp: 0.6, seed: 51 },
        { ri: 0.66, ro: 0.86, ts: 2.1, tl: TAU * 0.3, tx: -0.6, ty: 0.9, sp: -0.5, seed: 63 },
        { ri: 0.82, ro: 1.02, ts: 4.0, tl: TAU * 0.5, tx: 0.4, ty: -0.8, sp: 0.42, seed: 77 },
        { ri: 0.72, ro: 0.95, ts: 1.2, tl: TAU * 0.22, tx: 1.1, ty: 0.5, sp: -0.36, seed: 88 },
        { ri: 1.0, ro: 1.2, ts: 5.2, tl: TAU * 0.36, tx: -0.9, ty: -0.3, sp: 0.5, seed: 94 },
        { ri: 0.56, ro: 0.78, ts: 3.4, tl: TAU * 0.26, tx: 0.2, ty: 1.2, sp: -0.6, seed: 106 },
    ]
    for (const c of diskCfg) {
        const m = new THREE.MeshBasicMaterial({ color: COL.cyan, transparent: true, opacity: 0.42, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide })
        const disk = new THREE.Mesh(new THREE.RingGeometry(c.ri, c.ro, 72, 1, c.ts, c.tl), m)
        disk.rotation.x = c.tx; disk.rotation.y = c.ty; disk.rotation.z = seeded(c.seed) * TAU
        disk.userData = {
            isDisk: true, spin: c.sp, base: 0.42, cool: coolC, hot: briteC, seed: c.seed,
            nextJump: 0, jumpActive: false, jumpT: 0, jumpDur: 0, jumpTotal: 0, jumpPrevEased: 0,
            band: bandForRadius((c.ri + c.ro) / 2),
            ...fadeParams(c.seed + 1),
        }
        ringGroup.add(disk)
    }

    // segmented "gauge" arcs — thick irregular blocks; treated as disks (spin on normal + eased jumps)
    const gaugeCfg = [
        { ri: 0.86, ro: 1.08, ts: 0.4, tl: TAU * 0.62, count: 18, tx: 1.28, ty: 0.06, sp: 0.28, seed: 141 },
    ]
    for (const c of gaugeCfg) {
        const m = new THREE.MeshBasicMaterial({ color: COL.cyan, transparent: true, opacity: 0.4, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide })
        const gauge = new THREE.Mesh(segmentedArc(c.ri, c.ro, c.ts, c.tl, c.count, c.seed), m)
        gauge.rotation.x = c.tx; gauge.rotation.y = c.ty; gauge.rotation.z = seeded(c.seed) * TAU
        gauge.userData = {
            isDisk: true, spin: c.sp, base: 0.4, cool: coolC, hot: briteC, seed: c.seed,
            nextJump: 0, jumpActive: false, jumpT: 0, jumpDur: 0, jumpTotal: 0, jumpPrevEased: 0,
            band: bandForRadius((c.ri + c.ro) / 2),
            ...fadeParams(c.seed + 2),
        }
        ringGroup.add(gauge)
    }

    // irregular partial tick-mark arcs
    const tickCfg = [
        { r: 1.16, count: 64, tx: 0.9, ty: 0.3, sp: 0.22, seed: 191, span: TAU * 0.5 },
        { r: 0.52, count: 40, tx: -0.4, ty: 0.85, sp: -0.3, seed: 205, span: TAU * 0.45 },
    ]
    for (const c of tickCfg) {
        const m = new THREE.LineBasicMaterial({ color: COL.cer, transparent: true, opacity: 0.5, blending: THREE.AdditiveBlending, depthWrite: false })
        const ticks = new THREE.LineSegments(tickRing(c.r, c.count, c.seed, 0, c.span), m)
        ticks.rotation.x = c.tx; ticks.rotation.y = c.ty; ticks.rotation.z = seeded(c.seed) * TAU
        ticks.userData = mkDisk(c.seed, c.sp, 0.5, coolC, hotC)
        ticks.userData.band = bandForRadius(c.r)
        ringGroup.add(ticks)
    }

    // broken variable-thickness HUD outline rings (JARVIS-style); disks that stay mostly present
    const hudCfg = [
        { r: 1.15, ts: 0.5, tl: TAU * 0.8, tx: 1.16, ty: 0.12, sp: 0.16, seed: 221, fmin: 0.45 },
        { r: 0.78, ts: 3.1, tl: TAU * 0.66, tx: 0.9, ty: -0.4, sp: -0.2, seed: 236, fmin: 0.35 },
    ]
    for (const c of hudCfg) {
        const m = new THREE.MeshBasicMaterial({ color: COL.bright, transparent: true, opacity: 0.5, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide })
        const hud = new THREE.Mesh(hudArc(c.r, c.ts, c.tl, c.seed), m)
        hud.rotation.x = c.tx; hud.rotation.y = c.ty; hud.rotation.z = seeded(c.seed) * TAU
        hud.userData = {
            isDisk: true, spin: c.sp, base: 0.5, cool: hotC, hot: briteC, seed: c.seed,
            nextJump: 0, jumpActive: false, jumpT: 0, jumpDur: 0, jumpTotal: 0, jumpPrevEased: 0,
            band: bandForRadius(c.r),
            ...fadeParams(c.seed + 4, c.fmin),
        }
        ringGroup.add(hud)
    }

    let target: Target = { ...STATES.idle }
    const cur: Target = { ...target }
    let pulseEnergy = 0.15
    let paused = false, disposed = false, raf = 0
    let lw = 0, lh = 0
    const start = performance.now(); let last = start
    // core/dots periodically ease into reversing direction, so the motion never repeats
    let dirTarget = 1, dirCur = 1
    let nextFlip = start + 3000 + Math.random() * 12000
    // the outer (semi-transparent) core breathes: eases between full size and 30% larger than the inner core
    const CORE_MIN = 1.3 * (0.085 / 0.3) // ≈0.37 of full; keeps min outer = 1.3× inner core
    let breFrom = 1, breTo = CORE_MIN, breT = 0, breDur = 6 + Math.random() * 3

    function resize() {
        const parent = canvas.parentElement
        const w = Math.max(2, canvas.clientWidth || parent?.clientWidth || 300)
        const h = Math.max(2, canvas.clientHeight || parent?.clientHeight || 300)
        if (Math.abs(w - lw) < 0.5 && Math.abs(h - lh) < 0.5) return
        lw = w; lh = h
        renderer.setSize(w, h, false)
        camera.aspect = w / h; camera.updateProjectionMatrix()
    }

    function frame(now: number) {
        raf = 0
        if (paused || disposed) return
        const elapsed = (now - start) / 1000
        const dt = Math.min(0.05, (now - last) / 1000); last = now

        const s = 0.08
        cur.energy = lerp(cur.energy, target.energy, s)
        cur.rotation = lerp(cur.rotation, target.rotation, s)
        cur.particle = lerp(cur.particle, target.particle, s)
        cur.coreScale = lerp(cur.coreScale, target.coreScale, s)
        cur.bloom = lerp(cur.bloom, target.bloom, s)
        cur.ringSpread = lerp(cur.ringSpread, target.ringSpread, s)
        pulseEnergy *= 0.925
        const pulse = Math.min(1.7, pulseEnergy)
        const time = elapsed * cur.particle
        const act = Math.min(1, cur.energy * 0.9 + pulse * 0.5)

        // periodic smooth direction reversal for the core + dots
        if (now >= nextFlip) { dirTarget = -dirTarget; nextFlip = now + 3000 + Math.random() * 12000 }
        dirCur = lerp(dirCur, dirTarget, 0.012)

        root.rotation.x = Math.sin(elapsed * 0.32) * 0.05 // gentle drift only; rings carry the motion
        particles.rotation.y += dt * (0.16 + cur.rotation * 0.1) * dirCur
        particles.rotation.x += dt * 0.05 * dirCur

        // rays drift slowly and shimmer
        rays.rotation.y += dt * 0.07 * dirCur
        rays.rotation.x = Math.sin(elapsed * 0.22) * 0.08
        rayMat.opacity = (0.12 + 0.06 * Math.sin(elapsed * 0.5)) * (0.7 + cur.bloom * 0.5)
        rayMat.color.lerpColors(coolC, hotC, act)

        // outer-core breathing (eased, every 6–9s)
        breT += dt
        if (breT >= breDur) { breT = 0; breFrom = breTo; breTo = breTo === 1 ? CORE_MIN : 1; breDur = 6 + Math.random() * 3 }
        core.scale.setScalar(lerp(breFrom, breTo, easeInOut(Math.min(1, breT / breDur))))

        coreGroup.scale.setScalar(cur.coreScale + pulse * 0.06)
        ringGroup.scale.setScalar(cur.ringSpread + pulse * 0.03)
        halo.scale.setScalar(2.2 + cur.bloom * 0.8 + pulse * 0.5)
        const haloMat = halo.material as THREE.SpriteMaterial
        haloMat.opacity = 0.4 + cur.bloom * 0.35 + pulse * 0.25
        haloMat.color.lerpColors(coolC, hotC, act)

        coreMat.uniforms.uTime.value = elapsed
        coreMat.uniforms.uAct.value = act
        innerMat.opacity = 0.55 + cur.bloom * 0.3 + pulse * 0.3
        pMat.uniforms.uTime.value = time
        pMat.uniforms.uAct.value = act
        pMat.uniforms.uEnergy.value = cur.energy
        pMat.uniforms.uPulse.value = pulse

        for (const child of ringGroup.children) {
            const ud = child.userData as Record<string, any>
            // every element is a disk: slow spin on its own normal axis + periodic eased jumps of ≥60°
            child.rotateOnAxis(ZAXIS, dt * ud.spin * cur.rotation)
            if (ud.jumpActive) {
                ud.jumpT += dt
                const p = Math.min(1, ud.jumpT / ud.jumpDur)
                const eased = easeInOut(p)
                child.rotateOnAxis(ZAXIS, (eased - ud.jumpPrevEased) * ud.jumpTotal)
                ud.jumpPrevEased = eased
                if (p >= 1) { ud.jumpActive = false; ud.nextJump = now + 5000 + Math.random() * 18000 }
            } else if (ud.nextJump === 0) {
                ud.nextJump = now + 5000 + Math.random() * 16000
            } else if (now >= ud.nextJump) {
                ud.jumpActive = true; ud.jumpT = 0; ud.jumpPrevEased = 0
                ud.jumpTotal = (Math.random() < 0.5 ? -1 : 1) * (Math.PI / 3 + Math.random() * Math.PI * 0.9) // ≥60°
                ud.jumpDur = 0.6 + Math.random() * 0.8
            }
            // fade state machine: hold idle 10–30s, then ease to the next level (hidden holds run longer)
            if (ud.fHolding) {
                if (ud.fHoldUntil === 0) ud.fHoldUntil = now + ud.fHoldOffset + 2000
                else if (now >= ud.fHoldUntil) {
                    ud.fFrom = ud.fVal
                    if (ud.fVisible) { ud.fTarget = ud.fMin; ud.fVisible = false }
                    else { ud.fTarget = 0.72 + Math.random() * 0.28; ud.fVisible = true }
                    ud.fDur = 1.1 + Math.random() * 1.4
                    ud.fT = 0; ud.fHolding = false
                }
            } else {
                ud.fT += dt
                const p = Math.min(1, ud.fT / ud.fDur)
                ud.fVal = lerp(ud.fFrom, ud.fTarget, easeInOut(p))
                if (p >= 1) {
                    ud.fHolding = true
                    ud.fHoldUntil = ud.fVisible ? now + 10000 + Math.random() * 20000 : now + 15000 + Math.random() * 25000
                }
            }
            const fade = ud.fVal
            const mat = (child as THREE.Mesh).material as THREE.LineBasicMaterial | THREE.MeshBasicMaterial
            mat.opacity = ud.base * (0.8 + cur.bloom * 0.45 + pulse * 0.4) * fade
            mat.color.lerpColors(ud.cool as THREE.Color, ud.hot as THREE.Color, act)
        }
        core.rotation.x += dt * (0.18 + cur.rotation * 0.12) * dirCur
        core.rotation.y -= dt * (0.22 + cur.rotation * 0.18) * dirCur

        renderer.render(scene, camera)
        raf = requestAnimationFrame(frame)
    }

    resize()
    raf = requestAnimationFrame(frame)

    return {
        setState(state) { target = { ...STATES[state] }; pulseEnergy = Math.max(pulseEnergy, state === "speaking" ? 0.5 : 0.3) },
        pulse(amount = 1) { pulseEnergy = Math.max(pulseEnergy, amount) },
        resize,
        setPaused(p) {
            if (p === paused) return
            paused = p
            if (p) { if (raf) cancelAnimationFrame(raf); raf = 0 }
            else { last = performance.now(); if (!raf) raf = requestAnimationFrame(frame) }
        },
        dispose() {
            disposed = true
            if (raf) cancelAnimationFrame(raf)
            scene.traverse(o => {
                const m = o as THREE.Mesh
                m.geometry?.dispose?.()
                const mats = Array.isArray(m.material) ? m.material : m.material ? [m.material] : []
                mats.forEach(x => x.dispose())
            })
            ;(halo.material as THREE.SpriteMaterial).map?.dispose()
            renderer.dispose()
        },
    }
}
