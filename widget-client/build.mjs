import * as esbuild from "esbuild";
import { fileURLToPath } from "node:url";
import path from "node:path";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

await esbuild.build({
  entryPoints: [path.join(__dirname, "src/index.ts")],
  bundle: true,
  minify: true,
  format: "iife",
  target: ["es2020"],
  outfile: path.join(__dirname, "..", "backend", "wwwroot", "widget", "nivora-widget.js"),
  loader: { ".css": "text" },
  logLevel: "info",
});
