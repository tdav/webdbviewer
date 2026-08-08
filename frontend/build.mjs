import * as esbuild from 'esbuild';
import { cpSync, mkdirSync } from 'fs';
const outdir = '../src/WebDbViewer.Web/wwwroot/js';
mkdirSync(outdir, { recursive: true });
// HTMX хостится локально (air-gapped)
cpSync('node_modules/htmx.org/dist/htmx.min.js', `${outdir}/htmx.min.js`);
await esbuild.build({
  entryPoints: ['src/editor.js', 'src/grid.js', 'src/app.js', 'src/splitter.js'],
  bundle: true, minify: true, format: 'iife',
  outdir, globalName: undefined,
  logLevel: 'info'
});
