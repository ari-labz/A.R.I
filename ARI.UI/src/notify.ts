// Plays a short chime when an Ari response completes (issue #63). Swap src/assets/notification.mp3
// to change the sound.
import notifySrc from "./assets/notification.mp3"

const audio = new Audio(notifySrc)
audio.preload = "auto"
audio.volume = 0.5

// Browsers only allow audio after a user gesture, and that permission is "transient" — it expires a
// few seconds after the click. Because an Ari response usually takes longer than that to finish, a
// naive play() at completion is blocked. So we "unlock" the element once, inside the first real
// gesture (muted play→pause blesses it), after which programmatic play() is permitted indefinitely.
let unlocked = false
function unlock(): void {
    if (unlocked) return
    audio.muted = true
    audio.play().then(() => {
        audio.pause()
        audio.currentTime = 0
        audio.muted = false
        unlocked = true
        removeUnlockListeners()
    }).catch(() => { audio.muted = false })
}
function removeUnlockListeners(): void {
    window.removeEventListener("pointerdown", unlock)
    window.removeEventListener("keydown", unlock)
}
window.addEventListener("pointerdown", unlock)
window.addEventListener("keydown", unlock)

export function playResponseChime(): void {
    try {
        audio.currentTime = 0
        void audio.play().catch(err => {
            // Should be rare now that the element is unlocked; log so it's not a silent failure.
            console.warn("[notify] chime blocked:", err?.name ?? err)
        })
    } catch (err) {
        console.warn("[notify] chime error:", err)
    }
}
