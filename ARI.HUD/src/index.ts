// ARI.HUD — Ari's voice-mode face: a framework-agnostic canvas renderer plus its state and amplitude contracts.
// Host apps (ARI.UI today; a desktop/Discord overlay later) provide a canvas and drive setState/setLevel; all
// animation and camera work lives here, independent of any UI framework.

export { createHudFace, DEFAULT_VIEW } from "./face"
export type { HudFace, HudStateName, HudView } from "./face"
export { HUD_BANDS } from "./amplitude"
export type { HudLevels } from "./amplitude"
