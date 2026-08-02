using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

namespace Biomes.EditorTools
{
    /// <summary>
    /// Bakes the 12-epoch Shanghai built-up transect into a single StreamingAssets blob for
    /// <see cref="ShanghaiTransect"/>.
    ///
    /// <para><b>Why this reads the PNGs itself.</b> The source frames are 16-bit grayscale.
    /// Routing them through Unity's texture importer would quantise them to 8 bits unless every
    /// import setting is exactly right, and — worse — it would do so silently, producing a layer
    /// that looks plausible and is wrong. The same trap is why the design spec insists on pypng
    /// rather than Pillow on the Python side. Decoding here means the bake depends on no importer
    /// settings at all, and the baked blob is a plain array of the original uint16 samples.</para>
    ///
    /// <para>Output goes to StreamingAssets rather than an imported asset for the same reason:
    /// nothing between the source pixels and the GPU can reinterpret them.</para>
    /// </summary>
    public static class ShanghaiTransectBaker
    {
        // Grid contract shared by every TD_biomes layer (data/shanghai_growth/meta.json):
        // bbox 120.65, 30.60, 122.15, 31.90 WGS84 / EPSG:4326 at size_px 2048 — ~143 x 144 km,
        // so ~70 m/px.
        public const int SourceSize = 2048;
        public const int EpochCount = 12;

        // Venue cluster centre is pixel (1016, 1077) in source raster coordinates, where row 0
        // is the NORTH edge. The transect is centred on row 1077 so screen centre is the point
        // the audience stands at — saturated since before 1975, and the part of the frame the
        // 1.2 screen's cutout removes.
        public const int DefaultCentreRow = 1077;

        // 9472 x 900 master = 10.524:1, so 2048 / 10.524 = 195 rows -> 143 x 13.6 km.
        public const int DefaultTransectHeight = 195;

        public const string DefaultSourceDir =
            "/Users/toka/Developer/Graphics/TD_biomes/data/shanghai_growth";
        public const string OutputRelativePath = "StreamingAssets/biomes11/shanghai_transect.bytes";

        private const uint Magic = 0x52544853;   // "SHTR" little-endian
        private const int FormatVersion = 1;

        [MenuItem("Biomes/Bake Shanghai Transect")]
        public static void BakeDefault()
            => Bake(DefaultSourceDir, DefaultCentreRow, DefaultTransectHeight);

        public static void Bake(string sourceDir, int centreRow, int transectHeight)
        {
            if (!Directory.Exists(sourceDir))
            {
                Debug.LogError($"[ShanghaiTransectBaker] Source directory not found: {sourceDir}");
                return;
            }

            int half = transectHeight / 2;
            int firstRow = centreRow - half;
            if (firstRow < 0 || firstRow + transectHeight > SourceSize)
            {
                Debug.LogError($"[ShanghaiTransectBaker] Transect rows {firstRow}.." +
                               $"{firstRow + transectHeight - 1} fall outside the {SourceSize}px source.");
                return;
            }

            var frames = new List<ushort[]>(EpochCount);
            try
            {
                for (int i = 0; i < EpochCount; i++)
                {
                    string path = Path.Combine(sourceDir, $"shanghai_builtup_{i:00}.png");
                    if (!File.Exists(path))
                    {
                        Debug.LogError($"[ShanghaiTransectBaker] Missing epoch frame: {path}");
                        return;
                    }

                    EditorUtility.DisplayProgressBar("Baking Shanghai transect",
                        $"Decoding epoch {i + 1}/{EpochCount}", (i + 0.5f) / EpochCount);

                    var img = DecodePng16Gray(File.ReadAllBytes(path), out int w, out int h);
                    if (w != SourceSize || h != SourceSize)
                    {
                        Debug.LogError($"[ShanghaiTransectBaker] {Path.GetFileName(path)} is {w}x{h}, " +
                                       $"expected {SourceSize}x{SourceSize} — the grid contract has changed.");
                        return;
                    }
                    frames.Add(CropTransect(img, w, firstRow, transectHeight));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string outPath = Path.Combine(Application.dataPath, OutputRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(FormatVersion);
                bw.Write(SourceSize);        // transect width == full source width
                bw.Write(transectHeight);
                bw.Write(EpochCount);
                bw.Write(centreRow);
                foreach (var f in frames)
                    foreach (ushort v in f)
                        bw.Write(v);
            }

            AssetDatabase.Refresh();
            long bytes = new FileInfo(outPath).Length;
            Debug.Log($"[ShanghaiTransectBaker] Baked {EpochCount} epochs " +
                      $"{SourceSize}x{transectHeight} (rows {firstRow}..{firstRow + transectHeight - 1}, " +
                      $"centre {centreRow}) -> {outPath} ({bytes / 1024} KB)");
        }

        /// <summary>
        /// Crop the transect band and FLIP it vertically. Raster row 0 is the north edge;
        /// Unity texture row 0 is the bottom. Flipping here means the runtime can upload the
        /// buffer directly with no per-frame reorientation, and north stays up on screen.
        /// </summary>
        private static ushort[] CropTransect(ushort[] src, int width, int firstRow, int height)
        {
            var dst = new ushort[width * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = firstRow + (height - 1 - y);   // flip
                Array.Copy(src, srcRow * width, dst, y * width, width);
            }
            return dst;
        }

        // ── Minimal PNG decoder: 16-bit grayscale, non-interlaced ──────────────────────
        // Scoped deliberately narrowly. The source frames are all colour type 0 / depth 16 /
        // interlace 0 (verified against every epoch), and a decoder that accepts only what it
        // was written for will fail loudly if the data ever changes shape — which is the
        // behaviour we want, given the whole point is not to silently mis-read these files.
        private static ushort[] DecodePng16Gray(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png.Length < 8 ||
                png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
                throw new InvalidDataException("Not a PNG.");

            int w = 0, h = 0, depth = 0, colorType = -1, interlace = 0;
            var idat = new MemoryStream();

            int off = 8;
            while (off + 8 <= png.Length)
            {
                int len = ReadInt32BE(png, off);
                string type = System.Text.Encoding.ASCII.GetString(png, off + 4, 4);
                int dataOff = off + 8;

                if (type == "IHDR")
                {
                    w = ReadInt32BE(png, dataOff);
                    h = ReadInt32BE(png, dataOff + 4);
                    depth = png[dataOff + 8];
                    colorType = png[dataOff + 9];
                    interlace = png[dataOff + 12];
                }
                else if (type == "IDAT")
                {
                    // Multi-IDAT is normal for large images: the compressed stream is the
                    // concatenation of every IDAT payload, not one chunk each.
                    idat.Write(png, dataOff, len);
                }
                else if (type == "IEND") break;

                off = dataOff + len + 4;   // + CRC
            }

            if (depth != 16 || colorType != 0)
                throw new NotSupportedException(
                    $"Expected 16-bit grayscale (depth 16, colour type 0); got depth {depth}, " +
                    $"colour type {colorType}. Refusing to guess — a wrong read here looks correct.");
            if (interlace != 0)
                throw new NotSupportedException("Interlaced PNG is not supported.");

            byte[] raw = Inflate(idat.ToArray());

            const int bpp = 2;                       // bytes per pixel (one 16-bit sample)
            int stride = w * bpp;
            var outPixels = new ushort[w * h];
            var prev = new byte[stride];
            var cur = new byte[stride];

            int p = 0;
            for (int y = 0; y < h; y++)
            {
                if (p >= raw.Length)
                    throw new InvalidDataException("Truncated PNG data stream.");
                byte filter = raw[p++];
                Buffer.BlockCopy(raw, p, cur, 0, stride);
                p += stride;

                Unfilter(filter, cur, prev, stride, bpp);

                for (int x = 0; x < w; x++)
                    outPixels[y * w + x] = (ushort)((cur[x * 2] << 8) | cur[x * 2 + 1]);  // big-endian

                (prev, cur) = (cur, prev);
            }

            width = w; height = h;
            return outPixels;
        }

        private static void Unfilter(byte filter, byte[] cur, byte[] prev, int stride, int bpp)
        {
            switch (filter)
            {
                case 0: break;                                            // None
                case 1:                                                   // Sub
                    for (int i = bpp; i < stride; i++)
                        cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2:                                                   // Up
                    for (int i = 0; i < stride; i++)
                        cur[i] = (byte)(cur[i] + prev[i]);
                    break;
                case 3:                                                   // Average
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                    }
                    break;
                case 4:                                                   // Paeth
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        int pp = a + b - c;
                        int pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                        int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                        cur[i] = (byte)(cur[i] + pred);
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown PNG filter type {filter}.");
            }
        }

        /// <summary>Inflate a zlib stream. DeflateStream handles RAW deflate only, so the
        /// 2-byte zlib header is skipped (and the trailing Adler-32 simply ignored).</summary>
        private static byte[] Inflate(byte[] zlib)
        {
            if (zlib.Length < 2) throw new InvalidDataException("Empty zlib stream.");
            using var input = new MemoryStream(zlib, 2, zlib.Length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        private static int ReadInt32BE(byte[] b, int i)
            => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }
}
