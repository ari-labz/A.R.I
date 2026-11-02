// Ari's face: a real 3D point-sphere core inside a stack of flat layers (halos, tick rings, dashed rings, a dot
// field), drawn through one perspective camera on a 2D canvas with additive glow.
//
//   idle       the stack seen from an angle (the saved view), slowly orbiting, drifting and breathing
//   listening  same as idle, but the camera swings face-on and the layers close up
//   thinking   face-on, everything spins and reacts faster
//   speaking   face-on; the core's glow follows her voice and the voice ring pulses with it
//
// Dragging the face pivots the idle camera around the core; the view is saved per browser.

export type HudStateName = "idle" | "listening" | "thinking" | "speaking"

export interface HudView {
    pitch: number   // tilt, radians (negative = looking down on the stack)
    roll: number    // turn of the stack about its own axis
    yaw: number     // camera swing left/right around the core
    rot: number     // rotation of the whole picture
    shift: number   // how far front layers slide up-left and back layers down-right
    orbit: boolean  // camera slowly circles the stack
    drift: boolean  // picture rotation wanders and the turn jumps now and then
    breathe: boolean
}

export interface HudFace {
    setState: (state: HudStateName) => void
    /** Her voice loudness this frame, 0..1 (drives the core's glow and the voice ring while speaking). */
    setLevel: (level: number) => void
    resize: () => void
    setPaused: (paused: boolean) => void
    readonly view: HudView
    dispose: () => void
}

// Default idle angle: tilt 43°, swing 8°, turn 75°, view -70°, shift 0.45.
const deg = (d: number) => d * Math.PI / 180
export const DEFAULT_VIEW: HudView = { pitch: deg(-43), roll: deg(75), yaw: deg(8), rot: deg(-70), shift: .45, orbit: true, drift: true, breathe: true }
const VIEW_KEY = "ari-hud-view"

const SKY: Rgb = [202, 221, 226], TEAL: Rgb = [95, 168, 180]
type Rgb = [number, number, number]
const rgba = (c: Rgb, a: number) => `rgba(${c[0]},${c[1]},${c[2]},${a})`

type LayerKind = "field" | "ticks" | "dash" | "halo" | "core" | "voice"
interface Layer { z: number; kind: LayerKind; r?: number; n?: number; major?: number; arcs?: number; len?: number; speed?: number; w?: number; sweep?: boolean }

// Back to front. Layers get larger the further they sit from the core.
const LAYERS: Layer[] = [
    { z: -3.4, kind: "field", r: 3.9 },
    { z: -2.8, kind: "ticks", r: 3.75, n: 144, major: 12, speed: .02 },
    { z: -2.2, kind: "halo",  r: 3.4, arcs: 3, len: .55, speed: .10, w: 1.4 },
    { z: -1.6, kind: "dash",  r: 2.95, n: 60, speed: -.07 },
    { z: -1.0, kind: "halo",  r: 2.5, arcs: 2, len: 1.0, speed: -.18, w: 2.2 },
    { z: -.45, kind: "halo",  r: 1.85, arcs: 4, len: .5, speed: .32, w: 1.6 },
    { z: 0,    kind: "core" },
    { z: .55,  kind: "voice", r: 1.42 },
    { z: 1.1,  kind: "halo",  r: 2.15, arcs: 2, len: 1.3, speed: -.26, w: 2.6 },
    { z: 1.7,  kind: "dash",  r: 2.7, n: 36, speed: .12 },
    { z: 2.3,  kind: "halo",  r: 3.15, arcs: 3, len: .8, speed: .15, w: 1.8 },
    { z: 2.9,  kind: "ticks", r: 3.6, n: 96, major: 8, speed: -.04, sweep: true },
    { z: 3.4,  kind: "halo",  r: 3.95, arcs: 6, len: .22, speed: -.06, w: 1 },
]

// Camera per state (idle's pitch/roll/yaw come from the saved view). spread: how far apart the layers sit.
const VIEWS: Record<HudStateName, { pitch: number; roll: number; yaw: number; spread: number; tempo: number }> = {
    idle:      { pitch: 0, roll: 0, yaw: 0, spread: 1,   tempo: 1 },
    listening: { pitch: 0, roll: 0, yaw: 0, spread: .28, tempo: 1 },
    thinking:  { pitch: 0, roll: 0, yaw: 0, spread: .45, tempo: 4.5 },
    speaking:  { pitch: 0, roll: 0, yaw: 0, spread: .28, tempo: 1 },
}

const D = 15                                  // camera distance
const DRIFT_LIMIT = 20 * Math.PI / 180        // picture rotation wanders this far either side
const GLANCE_MAX = 120 * Math.PI / 180        // turn jumps stay this close to the chosen turn
const GLANCE_MIN_STEP = 15 * Math.PI / 180    // and always move at least this much

function loadView(): HudView {
    try { return { ...DEFAULT_VIEW, ...JSON.parse(localStorage.getItem(VIEW_KEY) || "{}") } } catch { return { ...DEFAULT_VIEW } }
}

// Breathing: the layer shift swells over ~3 s, holds, settles over ~3 s, rests. One breath every 8 s.
function breath(t: number): number {
    const p = (t / 8000) % 1, ss = (x: number) => x * x * (3 - 2 * x)
    return p < .375 ? ss(p / .375) : p < .5 ? 1 : p < .875 ? 1 - ss((p - .5) / .375) : 0
}

export function createHudFace(canvas: HTMLCanvasElement, options: { draggable?: boolean } = {}): HudFace {
    const c = canvas.getContext("2d")!
    const view = loadView()
    const save = () => { try { localStorage.setItem(VIEW_KEY, JSON.stringify(view)) } catch { /* private window: not remembered */ } }

    const N = 1100, core: [number, number, number][] = []
    for (let i = 0; i < N; i++) { const y = 1 - (i / (N - 1)) * 2, r = Math.sqrt(1 - y * y), th = i * 2.39996; core.push([Math.cos(th) * r, y, Math.sin(th) * r]) }

    let state: HudStateName = "idle", level = 0, S = 600, U = S / 10.5
    let openAmt = 1, layerZ = 0, effRot = 0, effShift = 0
    const cam = { pitch: view.pitch, roll: view.roll, yaw: view.yaw, spread: 1, tempo: 1, phase: 0, spinRoll: 0, glow: .15 }
    const drift = { rot: 0, vel: 0, segEnd: 0, segVel: 0, dir: 1 }
    const glance = { off: 0, target: 0, next: 0 }
    const hist = new Array<number>(96).fill(0)
    let last = 0, raf = 0, paused = false, disposed = false

    function P(x: number, y: number, z: number): [number, number, number, number] {
        const turn = cam.roll + cam.spinRoll + glance.off
        const cr = Math.cos(turn), sr = Math.sin(turn)
        const X = x * cr - y * sr, Y = x * sr + y * cr
        const cp = Math.cos(cam.pitch), sp = Math.sin(cam.pitch)
        const Y2 = Y * cp - z * sp, Z1 = Y * sp + z * cp
        // Yaw swings the camera sideways around the core; every rotation is about the core, so it stays centred.
        const cy = Math.cos(cam.yaw), sy = Math.sin(cam.yaw)
        const X3 = X * cy + Z1 * sy, Z2 = -X * sy + Z1 * cy, k = D / (D - Z2)
        // Only while the stack is open: rotate the whole picture, and slide each layer by its depth.
        let sx = X3 * k * U, sy2 = -Y2 * k * U
        const vr = effRot * openAmt, cv = Math.cos(vr), sv = Math.sin(vr)
        ;[sx, sy2] = [sx * cv - sy2 * sv, sx * sv + sy2 * cv]
        const sh = effShift * openAmt * layerZ * U * .55
        return [S / 2 + sx - sh, S / 2 + sy2 - sh, Z2, k]
    }
    function ring(z: number, r: number, a0: number, a1: number, steps: number) {
        c.beginPath()
        for (let i = 0; i <= steps; i++) {
            const a = a0 + (a1 - a0) * i / steps, p = P(Math.cos(a) * r, Math.sin(a) * r, z)
            if (i) c.lineTo(p[0], p[1]); else c.moveTo(p[0], p[1])
        }
    }

    function draw(t: number) {
        const dt = Math.min(.05, (t - last) / 1000 || 0); last = t
        const v = state === "idle" ? { ...VIEWS.idle, pitch: view.pitch, roll: view.roll, yaw: view.yaw } : VIEWS[state]
        const ease = 1 - Math.pow(.02, dt)   // ~1 s settle, frame-rate independent
        cam.pitch += (v.pitch - cam.pitch) * ease
        cam.roll += (v.roll - cam.roll) * ease
        cam.yaw += (v.yaw - cam.yaw) * ease
        cam.spread += (v.spread - cam.spread) * ease
        cam.tempo += (v.tempo - cam.tempo) * (1 - Math.pow(.1, dt))
        cam.phase += dt * cam.tempo
        cam.spinRoll += dt * ((cam.tempo - 1) * .12 + (state === "idle" && view.orbit ? .22 : 0))
        const speaking = state === "speaking" ? level : 0
        const glowTo = state === "speaking" ? .3 + level * 1.1 : state === "thinking" ? .28 + .08 * Math.sin(t / 120) : .16
        cam.glow += (glowTo - cam.glow) * (1 - Math.pow(.0005, dt))
        hist.push(speaking); hist.shift()
        const open = Math.max(0, (cam.spread - .28) / .72)   // 1 = stack open, 0 = closed
        openAmt = open
        U = S / 10.5 * (1 + .32 * (1 - open))

        // Idle drift: the picture's rotation wanders near the chosen angle, moving, pausing, now and then reversing.
        if (view.drift && state === "idle") {
            if (t > drift.segEnd) {
                const pause = Math.random() < .4
                if (Math.random() < .3) drift.dir = -drift.dir
                if (Math.abs(drift.rot) > DRIFT_LIMIT * .6) drift.dir = -Math.sign(drift.rot)
                drift.segVel = pause ? 0 : drift.dir * (.04 + Math.random() * .06)
                drift.segEnd = t + 1500 + Math.random() * 4000
            }
            // Glances: every 3-25 s the turn snaps 15-120 degrees, so now and then she turns or changes direction.
            if (t > glance.next) {
                let to: number
                do {
                    const step = GLANCE_MIN_STEP + Math.random() * (GLANCE_MAX - GLANCE_MIN_STEP)
                    to = glance.target + (Math.random() < .5 ? -step : step)
                } while (Math.abs(to) > GLANCE_MAX)
                glance.target = to
                glance.next = t + 3000 + Math.random() * 22000
            }
        } else { drift.segVel = -drift.rot * .8; glance.target = 0 }
        drift.vel += (drift.segVel - drift.vel) * (1 - Math.pow(.08, dt))
        drift.rot = Math.max(-DRIFT_LIMIT, Math.min(DRIFT_LIMIT, drift.rot + drift.vel * dt))
        glance.off += (glance.target - glance.off) * (1 - Math.pow(.003, dt))
        effRot = view.rot + drift.rot
        effShift = view.shift * (view.breathe ? .65 + .7 * breath(t) : 1)

        c.setTransform(1, 0, 0, 1, 0, 0)
        c.clearRect(0, 0, S, S)
        c.globalCompositeOperation = "lighter"
        c.lineCap = "round"
        const ph = cam.phase, px = S / 920   // line widths were tuned on a 920 px canvas

        for (const L of LAYERS) {
            const z = L.z * cam.spread
            layerZ = L.z / 3.4
            const k = P(0, 0, z)[3] * px
            if (L.kind === "field") {
                const r = L.r!
                for (let gx = -r; gx <= r; gx += .3) for (let gy = -r; gy <= r; gy += .3) {
                    const d = Math.hypot(gx, gy) / r; if (d > 1) continue
                    const a = (1 - d) * (.22 + .18 * Math.sin(ph * 2 + gx * 3 + gy * 2))
                    const p = P(gx, gy, z), s = 2.8 * px
                    c.fillStyle = rgba(TEAL, a); c.fillRect(p[0] - s / 2, p[1] - s / 2, s, s)
                }
            } else if (L.kind === "ticks") {
                const rot = ph * L.speed! * 2, r = L.r!
                for (let i = 0; i < L.n!; i++) {
                    const a = i / L.n! * Math.PI * 2 + rot, major = i % L.major! === 0, len = major ? .18 : .06
                    const p1 = P(Math.cos(a) * r, Math.sin(a) * r, z), p2 = P(Math.cos(a) * (r - len), Math.sin(a) * (r - len), z)
                    c.strokeStyle = rgba(SKY, major ? .6 : .28); c.lineWidth = 1.4 * k
                    c.beginPath(); c.moveTo(p1[0], p1[1]); c.lineTo(p2[0], p2[1]); c.stroke()
                }
                if (L.sweep) {   // a scanning arc that races round faster the busier she is
                    const a = ph * 1.6, busy = (cam.tempo - 1) / 3.5
                    ring(z, r + .08, a, a + .5 + .4 * busy, 30)
                    c.strokeStyle = rgba(SKY, .25 + .5 * busy); c.lineWidth = 3 * k; c.stroke()
                }
            } else if (L.kind === "dash") {
                const rot = ph * L.speed! * 2
                for (let i = 0; i < L.n!; i++) {
                    const a = i / L.n! * Math.PI * 2 + rot; ring(z, L.r!, a, a + Math.PI / L.n!, 4)
                    c.strokeStyle = rgba(TEAL, .55); c.lineWidth = 2.2 * k; c.stroke()
                }
            } else if (L.kind === "halo") {
                for (let j = 0; j < L.arcs!; j++) {
                    const a = ph * L.speed! * 2 + j / L.arcs! * Math.PI * 2
                    const len = L.len! * (1 + .25 * Math.sin(ph * .7 + j)) + speaking * .35
                    ring(z, L.r!, a, a + len, 48)
                    c.strokeStyle = rgba(TEAL, .35); c.lineWidth = L.w! * 5 * k; c.stroke()   // soft underglow
                    c.strokeStyle = rgba(SKY, .85); c.lineWidth = L.w! * 1.6 * k; c.stroke()
                }
            } else if (L.kind === "voice") {
                c.beginPath()
                for (let i = 0; i <= 192; i++) {
                    const a = i / 192 * Math.PI * 2, h = hist[(i * 3) % 96] * (.6 + .4 * Math.sin(i * 1.7 + t / 110))
                    const r = L.r! * (1 + h * .28 + .012 * Math.sin(ph * 2 + i * .3))
                    const p = P(Math.cos(a) * r, Math.sin(a) * r, z)
                    if (i) c.lineTo(p[0], p[1]); else c.moveTo(p[0], p[1])
                }
                c.strokeStyle = rgba(TEAL, .4); c.lineWidth = 7 * k; c.stroke()
                c.strokeStyle = rgba(SKY, .9); c.lineWidth = 2.2 * k; c.stroke()
            } else {
                const ctr = P(0, 0, z), g = cam.glow
                // Bloom: a wide soft halo plus a hot centre, both scaled by how much she's emitting.
                let rg = c.createRadialGradient(ctr[0], ctr[1], 0, ctr[0], ctr[1], U * (1.5 + g * 1.6))
                rg.addColorStop(0, rgba(TEAL, .25 + g * .45)); rg.addColorStop(.5, rgba(TEAL, .06 + g * .12)); rg.addColorStop(1, rgba(TEAL, 0))
                c.fillStyle = rg; c.fillRect(0, 0, S, S)
                // A real 3D sphere: points turn on their own axis, then go through the same camera as everything else.
                const spin = ph * .45, cs = Math.cos(spin), sn = Math.sin(spin), ct = Math.cos(.42), st = Math.sin(.42), R = 1.05
                for (const [x, y, zz] of core) {
                    const x1 = x * cs + zz * sn, z1 = -x * sn + zz * cs
                    const y2 = y * ct - z1 * st, z2 = y * st + z1 * ct
                    const p = P(x1 * R, y2 * R, z + z2 * R)
                    const d = ((p[2] - ctr[2]) / R + 1) / 2   // 0 back .. 1 front
                    c.fillStyle = rgba(d > .55 ? SKY : TEAL, (.18 + d * .7) * (.75 + g * .5))
                    const size = (1.1 + d * 2.2) * p[3] * px
                    c.fillRect(p[0] - size / 2, p[1] - size / 2, size, size)
                }
                rg = c.createRadialGradient(ctr[0], ctr[1], 0, ctr[0], ctr[1], U * (.5 + g * .9))
                rg.addColorStop(0, rgba(SKY, .1 + g * .55)); rg.addColorStop(1, rgba(SKY, 0))
                c.fillStyle = rg; c.fillRect(0, 0, S, S)
            }
        }
        c.globalCompositeOperation = "source-over"
    }

    const loop = (t: number) => {
        if (disposed) return
        if (!paused) draw(t)
        raf = requestAnimationFrame(loop)
    }

    function resize() {
        const box = canvas.getBoundingClientRect(), dpr = Math.min(2, window.devicePixelRatio || 1)
        S = Math.max(64, Math.round(Math.min(box.width, box.height || box.width) * dpr))
        canvas.width = canvas.height = S
    }

    // Drag to pivot the idle camera around the core: sideways swings it, up/down tilts it.
    let drag: { x: number; y: number } | null = null
    const down = (e: PointerEvent) => { drag = { x: e.clientX, y: e.clientY }; canvas.setPointerCapture(e.pointerId) }
    const move = (e: PointerEvent) => {
        if (!drag) return
        view.yaw = Math.max(-1.3, Math.min(1.3, view.yaw + (e.clientX - drag.x) * .008))
        view.pitch = Math.max(-1.5, Math.min(.6, view.pitch + (e.clientY - drag.y) * .008))
        drag = { x: e.clientX, y: e.clientY }
    }
    const up = () => { if (drag) { drag = null; save() } }
    if (options.draggable) {
        canvas.addEventListener("pointerdown", down)
        canvas.addEventListener("pointermove", move)
        canvas.addEventListener("pointerup", up)
        canvas.addEventListener("pointercancel", up)
    }

    resize()
    raf = requestAnimationFrame(loop)

    return {
        setState(s) { state = s },
        setLevel(l) { level = Math.max(0, Math.min(1, l)) },
        resize,
        setPaused(p) { paused = p },
        view,
        dispose() {
            disposed = true
            cancelAnimationFrame(raf)
            canvas.removeEventListener("pointerdown", down)
            canvas.removeEventListener("pointermove", move)
            canvas.removeEventListener("pointerup", up)
            canvas.removeEventListener("pointercancel", up)
        },
    }
}
