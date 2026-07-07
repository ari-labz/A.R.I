// The orb's amplitude contract. An analyser (e.g. ARI.UI's mic/playback listener) writes ORB_BANDS
// levels per frame, core → outer, and the renderer reads them to light up like a radial equaliser.

export const ORB_BANDS = 5   // bass, low, mid, high, air → core … outer

/** Per-frame amplitude levels shared with the renderer: length ORB_BANDS, each 0..1. */
export type OrbLevels = number[]
