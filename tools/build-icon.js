const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const svgPath = process.argv[2];
const outDir = process.argv[3];
const svg = fs.readFileSync(svgPath);

const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

(async () => {
  const pngs = [];
  for (const size of sizes) {
    const buf = await sharp(svg, { density: Math.max(384, size * 4) })
      .resize(size, size, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
      .png({ compressionLevel: 9 })
      .toBuffer();
    pngs.push({ size, buf });
    if (size === 256) fs.writeFileSync(path.join(outDir, 'app-icon-256.png'), buf);
  }

  // ICO: ICONDIR + ICONDIRENTRY* + PNG data
  const count = pngs.length;
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(count, 4);

  const entries = Buffer.alloc(16 * count);
  let offset = 6 + 16 * count;
  for (let i = 0; i < count; i++) {
    const { size, buf } = pngs[i];
    const e = i * 16;
    entries.writeUInt8(size >= 256 ? 0 : size, e + 0);
    entries.writeUInt8(size >= 256 ? 0 : size, e + 1);
    entries.writeUInt8(0, e + 2);
    entries.writeUInt8(0, e + 3);
    entries.writeUInt16LE(1, e + 4);
    entries.writeUInt16LE(32, e + 6);
    entries.writeUInt32LE(buf.length, e + 8);
    entries.writeUInt32LE(offset, e + 12);
    offset += buf.length;
  }

  const ico = Buffer.concat([header, entries, ...pngs.map((p) => p.buf)]);
  fs.writeFileSync(path.join(outDir, 'app.ico'), ico);
  console.log('ico bytes=' + ico.length + ' sizes=' + sizes.join(','));
})();
