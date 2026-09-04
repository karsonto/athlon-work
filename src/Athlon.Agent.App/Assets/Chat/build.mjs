import * as esbuild from 'esbuild';
import { fileURLToPath } from 'url';
import path from 'path';

const dir = path.dirname(fileURLToPath(import.meta.url));

await esbuild.build({
  entryPoints: [path.join(dir, 'chat-timeline.entry.js')],
  bundle: true,
  format: 'iife',
  outfile: path.join(dir, 'chat-timeline.bundle.js'),
  platform: 'browser',
  target: ['chrome100'],
  legalComments: 'none'
});

console.log('Built chat-timeline.bundle.js');
