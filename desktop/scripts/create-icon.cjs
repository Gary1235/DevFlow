// Small code-drawn application mark, shared by the window, tray and installer.
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const name = Buffer.from(type);
  const size = Buffer.alloc(4); size.writeUInt32BE(data.length);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(Buffer.concat([name, data])));
  return Buffer.concat([size, name, data, crc]);
}
const size = 256;
const pixels = Buffer.alloc(size * (size * 4 + 1));
for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
  const offset = y * (size * 4 + 1) + 1 + x * 4;
  const inside = x >= 12 && x < 244 && y >= 12 && y < 244;
  const stem = x >= 65 && x < 92 && y >= 57 && y < 199;
  const ring = x >= 89 && x <= 197 && Math.abs(Math.hypot((x - 112) * 0.9, y - 128) - 57) <= 14;
  const ink = stem || ring;
  pixels.set(ink ? [232, 245, 255, 255] : [35, 73, 192, inside ? 255 : 0], offset);
}
const header = Buffer.alloc(13);
header.writeUInt32BE(size, 0); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6;
const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', zlib.deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))]);
const target = path.resolve(__dirname, '../src');
fs.mkdirSync(target, { recursive: true });
fs.writeFileSync(path.join(target, 'icon.png'), png);
const ico = Buffer.alloc(22);
ico.writeUInt16LE(1, 2); ico.writeUInt16LE(1, 4);
ico.writeUInt16LE(1, 10); ico.writeUInt16LE(32, 12);
ico.writeUInt32LE(png.length, 14); ico.writeUInt32LE(22, 18);
fs.writeFileSync(path.join(target, 'icon.ico'), Buffer.concat([ico, png]));
