// Web Push registration for the PWA. Registers the service worker, requests notification permission,
// subscribes to push, and hands the subscription to the backend so Ari can ring the phone. Best-effort:
// any failure (unsupported browser, denied permission, no VAPID key) is logged and swallowed.

function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
    const padding = "=".repeat((4 - (base64.length % 4)) % 4)
    const b64 = (base64 + padding).replace(/-/g, "+").replace(/_/g, "/")
    const raw = atob(b64)
    const out = new Uint8Array(new ArrayBuffer(raw.length))
    for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i)
    return out
}

export async function initPush(): Promise<void> {
    if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) return

    let reg: ServiceWorkerRegistration
    try {
        reg = await navigator.serviceWorker.register("/sw.js")
    } catch (e) {
        console.warn("[Push] service worker registration failed", e)
        return
    }

    // Only prompt when we haven't been answered yet. If already denied, respect that and stop.
    if (Notification.permission === "denied") return
    if (Notification.permission === "default") {
        const perm = await Notification.requestPermission().catch(() => "default" as NotificationPermission)
        if (perm !== "granted") return
    }

    try {
        const keyRes = await fetch("/push/vapid-public-key")
        if (!keyRes.ok) return
        const { publicKey } = await keyRes.json()
        if (!publicKey) return

        const existing = await reg.pushManager.getSubscription()
        const sub = existing ?? await reg.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: urlBase64ToUint8Array(publicKey),
        })

        await fetch("/push/subscribe", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(sub.toJSON()),
        })
    } catch (e) {
        console.warn("[Push] subscription failed", e)
    }
}
