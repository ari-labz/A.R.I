/** Starts the first-run guided tour. The engine itself is plain JS (public/js/tour.js) because it also has to run on the static control panel. */
export function startTour(): void {
    const w = window as unknown as { ariTour?: { start: () => void } }
    w.ariTour?.start()
}
