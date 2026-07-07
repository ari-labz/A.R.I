// Plays a short chime when an Ari response completes (issue #63). Swap src/assets/notification.wav
// (or point NOTIFY_SRC at an .mp3) to change the sound.
import notifySrc from "./assets/notification.wav"

const audio = new Audio(notifySrc)
audio.preload = "auto"
audio.volume = 0.5

// Reuse one element; clone-free replay by rewinding. Browsers gate audio until the first user
// gesture, but by the time a response finishes the user has already interacted, so play() resolves.
export function playResponseChime(): void {
    try {
        audio.currentTime = 0
        void audio.play().catch(() => {})   // ignore autoplay-policy rejections
    } catch {
        /* no-op: audio unavailable */
    }
}
