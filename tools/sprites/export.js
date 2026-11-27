#!/usr/bin/env node
// Export the pet sprites from the sprite book into the app's assets.
//
//   node tools/sprites/export.js            (style B)
//   node tools/sprites/export.js --style A
//
// The sprite book (tools/sprites/sprite-book.html) is the single source of
// the artwork: its <script id="engine"> block draws every frame. This script
// runs that same block and writes, per animation, one horizontal strip PNG
// (32x32 frames, left to right) plus pet.json with the frame durations.
// No npm packages: the PNG encoder below only needs Node's zlib.
'use strict';
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const root = path.resolve(__dirname, '..', '..');
const bookPath = path.join(__dirname, 'sprite-book.html');
const outDir = path.join(root, 'app', 'assets', 'pet');

const args = process.argv.slice(2);
const styleArg = args.indexOf('--style');
const style = styleArg >= 0 ? args[styleArg + 1] : 'B';

function loadEngine() {
  const html = fs.readFileSync(bookPath, 'utf8');
  const m = html.match(/<script id="engine">([\s\S]*?)<\/script>/);
  if (!m) throw new Error('sprite-book.html has no <script id="engine"> block');
  const mod = { exports: {} };
  new Function('module', m[1])(mod);
  return mod.exports;
}

function crc32(buf) {
  let crc = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++) {
    crc ^= buf[i];
    for (let k = 0; k < 8; k++) crc = (crc & 1) ? (crc >>> 1) ^ 0xEDB88320 : (crc >>> 1);
  }
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

// 8-bit RGBA, no interlace, filter 0 on every row.
function encodePng(width, height, rgba) {
  const stride = width * 4 + 1;
  const raw = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) rgba.copy(raw, y * stride + 1, y * width * 4, (y + 1) * width * 4);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const SPR = loadEngine();
if (!SPR.STYLES[style]) {
  console.error('unknown style "' + style + '". known: ' + Object.keys(SPR.STYLES).join(', '));
  process.exit(2);
}
const rgb = {};
for (const k of Object.keys(SPR.PAL)) {
  const h = SPR.PAL[k][0];
  rgb[k] = [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
}

fs.mkdirSync(outDir, { recursive: true });
const W = SPR.W, H = SPR.H;
const meta = { frame: W, ground: SPR.GROUND, style, hit: null, anims: {} };

for (const anim of SPR.ANIMS) {
  const n = anim.frames.length;
  const strip = Buffer.alloc(W * n * H * 4);
  for (let f = 0; f < n; f++) {
    const grid = SPR.grid(style, anim.id, f, false);
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const key = grid[y * W + x];
      if (!key) continue;
      const o = (y * W * n + f * W + x) * 4;
      strip[o] = rgb[key][0]; strip[o + 1] = rgb[key][1]; strip[o + 2] = rgb[key][2]; strip[o + 3] = 255;
    }
  }
  const file = anim.id + '.png';
  fs.writeFileSync(path.join(outDir, file), encodePng(W * n, H, strip));
  meta.anims[anim.id] = { file, ms: anim.frames.map(fr => fr.ms) };
}

// Clickable box: the painted bounds of the standing pose (idle, frame 1).
{
  const g = SPR.grid(style, 'idle', 0, false);
  let x0 = W, y0 = H, x1 = -1, y1 = -1;
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) if (g[y * W + x]) {
    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
  }
  meta.hit = { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
}

fs.writeFileSync(path.join(outDir, 'pet.json'), JSON.stringify(meta, null, 2) + '\n');
console.log('style ' + style + ' -> ' + path.relative(root, outDir));
for (const id of Object.keys(meta.anims)) {
  const a = meta.anims[id];
  console.log('  ' + a.file.padEnd(12) + String(a.ms.length).padStart(3) + ' frames  ' + a.ms.reduce((s, v) => s + v, 0) + ' ms');
}
console.log('  hit box ' + JSON.stringify(meta.hit));
