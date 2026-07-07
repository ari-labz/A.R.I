// Browser mic client for ARI.Listener: captures the microphone, downsamples to 16 kHz mono 16-bit PCM,
// and streams it to /api/listener/stream. Receives back {type:"transcript", text, addressed} events, plus
// Ari's spoken reply as binary WAV frames. While she speaks, the playback is analysed into frequency bands
// (low → high) so the orb can light up like a radial equaliser.

import { ORB_BANDS } from "ari-orb"

export interface ListenerEvent {
    type: string          // "ready" | "partial" | "transcript" | "error"
    text?: string
    addressed?: boolean
    message?: string
}

export interface ListenerHandle {
    stop: () => void
}

// Frequency band edges (Hz). Voice energy lives ~80 Hz–6 kHz; these split it low→high across the orb.
const BAND_EDGES_HZ = [50, 200, 500, 1200, 3000, 8000]

function clampToInt16(f: number): number {
    const s = Math.max(-1, Math.min(1, f))
    return s < 0 ? s * 0x8000 : s * 0x7fff
}

// Nearest-neighbour downsample from the mic rate to 16 kHz, packed as Int16 PCM.
function downsampleToInt16(input: Float32Array, inRate: number): ArrayBuffer {
    const outRate = 16000
    if (inRate === outRate) {
        const out = new Int16Array(input.length)
        for (let i = 0; i < input.length; i++) out[i] = clampToInt16(input[i])
        return out.buffer
    }
    const ratio = inRate / outRate
    const outLen = Math.floor(input.length / ratio)
    const out = new Int16Array(outLen)
    for (let i = 0; i < outLen; i++) out[i] = clampToInt16(input[Math.floor(i * ratio)])
    return out.buffer
}

function computeBands(freq: Uint8Array, sampleRate: number, fftSize: number, out: number[]): void {
    const binHz = sampleRate / fftSize
    for (let b = 0; b < ORB_BANDS; b++) {
        const i0 = Math.max(0, Math.floor(BAND_EDGES_HZ[b] / binHz))
        const i1 = Math.min(freq.length - 1, Math.ceil(BAND_EDGES_HZ[b + 1] / binHz))
        let sum = 0, n = 0
        for (let i = i0; i <= i1; i++) { sum += freq[i]; n++ }
        const avg = n > 0 ? sum / n / 255 : 0
        out[b] = Math.min(1, Math.pow(avg, 0.85) * 1.5) // gamma + gain so quiet speech still registers
    }
}

// `levels` is an optional shared ref (length ORB_BANDS) the analyser writes each frame; the orb reads it.
export async function startListening(
    threadKey: string,
    onEvent: (e: ListenerEvent) => void,
    levels?: { current: number[] },
): Promise<ListenerHandle> {
    const wsProto = location.protocol === "https:" ? "wss" : "ws"
    const ws = new WebSocket(`${wsProto}://${location.host}/api/listener/stream?source=web&threadKey=${encodeURIComponent(threadKey)}`)
    ws.binaryType = "arraybuffer"

    const stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    })
    const ctx = new AudioContext()
    const source = ctx.createMediaStreamSource(stream)
    const processor = ctx.createScriptProcessor(4096, 1, 1)
    const inRate = ctx.sampleRate

    processor.onaudioprocess = ev => {
        if (ws.readyState !== WebSocket.OPEN) return
        const pcm = downsampleToInt16(ev.inputBuffer.getChannelData(0), inRate)
        if (pcm.byteLength) ws.send(pcm)
    }
    source.connect(processor)
    processor.connect(ctx.destination) // required for the processor to run; it emits no audio itself

    // Analyser sits between Ari's playback and the speakers (not the mic), so the orb reacts to her voice only.
    const analyser = ctx.createAnalyser()
    analyser.fftSize = 1024
    analyser.smoothingTimeConstant = 0.75
    analyser.connect(ctx.destination)
    const freq = new Uint8Array(analyser.frequencyBinCount)
    let rafId = 0
    const tick = () => {
        analyser.getByteFrequencyData(freq)
        if (levels) computeBands(freq, ctx.sampleRate, analyser.fftSize, levels.current)
        rafId = requestAnimationFrame(tick)
    }
    rafId = requestAnimationFrame(tick)

    // Ari's spoken reply arrives as binary WAV frames (one per sentence). Play them back-to-back so it
    // sounds continuous; a new turn flushes what's queued. Decoding is chained to preserve order.
    let nextStart = 0
    const active = new Set<AudioBufferSourceNode>()
    let playChain: Promise<void> = Promise.resolve()

    const playWav = (data: ArrayBuffer) => {
        playChain = playChain.then(async () => {
            const buf = await ctx.decodeAudioData(data.slice(0))
            const src = ctx.createBufferSource()
            src.buffer = buf
            src.connect(analyser) // → analyser → destination (audible + analysed)
            const start = Math.max(ctx.currentTime + 0.02, nextStart)
            src.start(start)
            nextStart = start + buf.duration
            active.add(src)
            src.onended = () => active.delete(src)
        }).catch(() => { /* skip undecodable frame */ })
    }
    const flushAudio = () => {
        active.forEach(s => { try { s.stop() } catch { /* ignore */ } })
        active.clear()
        nextStart = ctx.currentTime
    }

    ws.onmessage = e => {
        if (typeof e.data !== "string") { playWav(e.data as ArrayBuffer); return } // binary = audio
        try {
            const evt = JSON.parse(e.data)
            if (evt.type === "thinking") flushAudio() // a new turn supersedes queued speech
            onEvent(evt)
        } catch { /* ignore */ }
    }

    let stopped = false
    const stop = () => {
        if (stopped) return
        stopped = true
        cancelAnimationFrame(rafId)
        if (levels) levels.current.fill(0)
        flushAudio()
        try { processor.disconnect(); source.disconnect() } catch { /* ignore */ }
        stream.getTracks().forEach(t => t.stop())
        void ctx.close().catch(() => {})
        try {
            if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "end" }))
            ws.close()
        } catch { /* ignore */ }
    }

    return { stop }
}
