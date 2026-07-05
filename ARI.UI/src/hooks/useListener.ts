// Browser mic client for ARI.Listener: captures the microphone, downsamples to 16 kHz mono 16-bit PCM,
// and streams it to /api/listener/stream. Receives back {type:"transcript", text, addressed} events.

export interface ListenerEvent {
    type: string          // "ready" | "partial" | "transcript" | "error"
    text?: string
    addressed?: boolean
    message?: string
}

export interface ListenerHandle {
    stop: () => void
}

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

export async function startListening(threadKey: string, onEvent: (e: ListenerEvent) => void): Promise<ListenerHandle> {
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
            src.connect(ctx.destination)
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
