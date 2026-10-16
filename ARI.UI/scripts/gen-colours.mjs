// Generates public/css/colours.css from public/colours.json. Run after editing colours.json:
//   node scripts/gen-colours.mjs
import { readFileSync, writeFileSync } from "node:fs"

const { colours } = JSON.parse(readFileSync(new URL("../public/colours.json", import.meta.url), "utf8"))
const out = ["/* Generated from colours.json by scripts/gen-colours.mjs — edit colours.json, not this file. */", ":root {"]
for (const [name, c] of Object.entries(colours)) out.push(`    --col-${name}: ${c.value};`)
out.push("}", "")
writeFileSync(new URL("../public/css/colours.css", import.meta.url), out.join("\n"))
console.log(`colours.css: ${Object.keys(colours).length} colours`)
