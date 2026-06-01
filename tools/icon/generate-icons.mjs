/**
 * Regenerate WPF / installer icons from Assets/app-icon.svg
 * (source: athlon/report/html/public/athlon-icon.svg)
 *
 * Usage: npm install && npm run generate
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Resvg } from '@resvg/resvg-js';
import pngToIco from 'png-to-ico';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const assetsDir = path.resolve(__dirname, '../../src/Athlon.Agent.App/Assets');
const svgPath = path.join(assetsDir, 'app-icon.svg');

if (!fs.existsSync(svgPath)) {
  console.error('Missing', svgPath);
  process.exit(1);
}

const svg = fs.readFileSync(svgPath);
const pngSizes = [48, 64, 128, 256];

for (const size of pngSizes) {
  const resvg = new Resvg(svg, { fitTo: { mode: 'width', value: size } });
  const out =
    size === 256
      ? path.join(assetsDir, 'app-icon-256.png')
      : path.join(assetsDir, `app-icon-${size}.png`);
  fs.writeFileSync(out, resvg.render().asPng());
  console.log('wrote', path.basename(out));
}

const ico = await pngToIco(
  pngSizes.map((s) =>
    path.join(assetsDir, s === 256 ? 'app-icon-256.png' : `app-icon-${s}.png`),
  ),
);
fs.writeFileSync(path.join(assetsDir, 'app-icon.ico'), ico);
console.log('wrote app-icon.ico');

fs.unlinkSync(path.join(assetsDir, 'app-icon-256.png'));
console.log('removed app-icon-256.png (ICO intermediate only)');
