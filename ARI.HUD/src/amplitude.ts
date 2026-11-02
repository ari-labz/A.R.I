// The HUD's amplitude contract. An analyser (e.g. ARI.UI's playback listener) writes HUD_BANDS levels per frame,
// low → high; the host averages them into the single loudness the face reacts to.

export const HUD_BANDS = 5   // bass, low, mid, high, air

/** Per-frame amplitude levels: length HUD_BANDS, each 0..1. */
export type HudLevels = number[]
