import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import App from "./App"
import { startVisuals } from "./visuals"
import { startModels } from "./models"

startVisuals()
startModels()
createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <App />
    </StrictMode>
)

