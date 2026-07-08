// Regenerates badges/*.svg. Run after changing license, target framework, or protocol version.
// Usage: node scripts/generate-badges.js
const fs = require("fs");
const path = require("path");
const { makeBadge } = require("badge-maker");

const badges = {
  "license.svg": { label: "license", message: "Apache-2.0", color: "blue" },
  "dotnet.svg": { label: ".NET", message: "10", color: "512BD4" },
  "mcp.svg": { label: "MCP", message: "Model Context Protocol", color: "0B7285" },
};

const outDir = path.join(__dirname, "..", "badges");
for (const [file, format] of Object.entries(badges)) {
  fs.writeFileSync(path.join(outDir, file), makeBadge(format));
}
