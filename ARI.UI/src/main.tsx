import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import App from "./App"
import { initPush } from "./push"

createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <App />
    </StrictMode>
)

// Register the service worker + push subscription (best-effort; no-op on unsupported browsers).
initPush()

