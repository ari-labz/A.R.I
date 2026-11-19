import { apiFetch } from "./auth"

// 3D models Ari makes (image_to_mesh) or hands over (deliver_file) as .fbx / .glb: the message holds an empty
// <div class="ari-model" data-model-src>, and this fills it with a viewer you can drag to orbit and scroll to zoom.
// three.js loads on first use, so chats without a model never pay for it.

export const MODEL_EXTS = ["fbx", "glb", "gltf"]

async function mount(el: HTMLElement) {
    const src = el.dataset.modelSrc
    if (!src || el.dataset.mounted) return
    el.dataset.mounted = "1"
    try {
        const THREE = await import("three")
        const { OrbitControls } = await import("three/examples/jsm/controls/OrbitControls.js")
        const ext = (el.dataset.modelName ?? "").split(".").pop()!.toLowerCase()
        const r = await apiFetch(src)
        if (!r.ok) throw new Error(String(r.status))
        const buf = await r.arrayBuffer()

        let object: import("three").Object3D
        if (ext === "fbx") {
            const { FBXLoader } = await import("three/examples/jsm/loaders/FBXLoader.js")
            object = new FBXLoader().parse(buf, "")
        } else {
            const { GLTFLoader } = await import("three/examples/jsm/loaders/GLTFLoader.js")
            object = (await new GLTFLoader().parseAsync(buf, "")).scene
        }

        const canvas = document.createElement("canvas")
        canvas.style.cssText = "display:block;width:100%;height:340px;cursor:grab;touch-action:none"
        const hint = document.createElement("div")
        hint.className = "ari-model-hint"
        hint.textContent = "Drag to turn · scroll to zoom · double-click to reset"
        el.replaceChildren(canvas, hint)

        const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true })
        renderer.setPixelRatio(Math.min(devicePixelRatio, 2))
        renderer.outputColorSpace = THREE.SRGBColorSpace
        const scene = new THREE.Scene()
        scene.add(new THREE.HemisphereLight(0xffffff, 0x8899aa, 1.6))
        const key = new THREE.DirectionalLight(0xffffff, 2.2); key.position.set(3, 5, 4); scene.add(key)
        // Plain lit materials: the FBX loader's Phong materials come out black here (white emissive) and its textures are not flagged sRGB.
        object.traverse(c => {
            const mesh = c as import("three").Mesh
            if (!mesh.isMesh) return
            const old = (Array.isArray(mesh.material) ? mesh.material[0] : mesh.material) as import("three").MeshPhongMaterial
            if (old.map) old.map.colorSpace = THREE.SRGBColorSpace
            mesh.material = new THREE.MeshStandardMaterial({ map: old.map ?? null, color: old.map ? 0xffffff : old.color, roughness: 0.85, metalness: 0, side: THREE.DoubleSide })
        })
        scene.add(object)

        // Centre it and scale it to a unit size so any model fills the frame.
        const box = new THREE.Box3().setFromObject(object)
        const size = Math.max(...box.getSize(new THREE.Vector3()).toArray()) || 1
        const centre = box.getCenter(new THREE.Vector3())
        object.position.sub(centre)
        object.scale.multiplyScalar(1.7 / size)

        const camera = new THREE.PerspectiveCamera(40, 1, 0.01, 100)
        // Look at the broad side: the camera goes on the axis along which the model is thinnest, a little raised and to the side.
        const dims = box.getSize(new THREE.Vector3())
        const thin = dims.x <= dims.y && dims.x <= dims.z ? "x" : dims.y <= dims.z ? "y" : "z"
        const home = thin === "x" ? new THREE.Vector3(2.1, 0.8, 0.6) : thin === "y" ? new THREE.Vector3(0.6, 2.1, 0.8) : new THREE.Vector3(0.6, 0.8, 2.1)
        camera.position.copy(home)
        const controls = new OrbitControls(camera, canvas)
        controls.enableDamping = true
        controls.autoRotate = true
        controls.autoRotateSpeed = 1.2
        controls.addEventListener("start", () => { controls.autoRotate = false })
        canvas.addEventListener("dblclick", () => { camera.position.copy(home); controls.target.set(0, 0, 0); controls.autoRotate = true })

        const resize = () => {
            const w = canvas.clientWidth, h = canvas.clientHeight
            if (w && (canvas.width !== Math.floor(w * renderer.getPixelRatio()) || canvas.height !== Math.floor(h * renderer.getPixelRatio()))) {
                renderer.setSize(w, h, false); camera.aspect = w / h; camera.updateProjectionMatrix()
            }
        }
        const tick = () => {
            if (!el.isConnected) { renderer.dispose(); return }   // the message left the page
            resize(); controls.update(); renderer.render(scene, camera)
            requestAnimationFrame(tick)
        }
        tick()
    } catch (e) {
        console.error("Model viewer failed", e)
        el.textContent = "Couldn't show this model. Download it from the card above."
    }
}

let started = false
export function startModels() {
    if (started) return
    started = true
    const scan = (root: ParentNode) => root.querySelectorAll?.<HTMLElement>(".ari-model:not([data-mounted])").forEach(mount)
    new MutationObserver(muts => {
        for (const m of muts) m.addedNodes.forEach(n => { if (n instanceof HTMLElement) { if (n.matches?.(".ari-model")) mount(n); else scan(n) } })
    }).observe(document.body, { childList: true, subtree: true })
    scan(document)
}
