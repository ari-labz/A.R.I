// ARI.Orb — ARI's voice-mode orb. A framework-agnostic WebGL renderer plus its state and amplitude
// contracts. Host apps (ARI.UI today; a desktop/Discord overlay later) provide a canvas and drive
// setState/pulse/levels; all animation and lighting work lives here, independent of any UI framework.

export { createOrbRenderer } from "./renderer"
export type { OrbRenderer, OrbStateName } from "./renderer"
export { ORB_BANDS } from "./amplitude"
export type { OrbLevels } from "./amplitude"
