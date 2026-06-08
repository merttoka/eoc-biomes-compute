#!/usr/bin/env python3
"""Convert normalized_neuron_data.csv (frames x 3*neurons) to a compact float16 blob.

Output layout (little-endian):
  magic  : 4 bytes  b"TFR1"
  uint32 : neuronCount        (z-columns = ncols // 3)
  uint32 : frameCount
  data   : frameCount * neuronCount  float16   (row-major: frame, then neuron)

Only the z column of each (x,y,z) triple is kept: z = col[n*3 + 2].
Streams line-by-line — never loads the whole (729 MB) file into memory.
"""
import sys, struct
import numpy as np


def main(src, dst):
    neuron_count = None
    frames = 0
    with open(src, "r") as f, open(dst, "wb") as out:
        out.write(b"TFR1")
        out.write(struct.pack("<II", 0, 0))  # placeholder header, rewritten at end
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split(",")
            # header / malformed rows: any z fails float() -> skip
            try:
                zs = [float(parts[i * 3 + 2]) for i in range(len(parts) // 3)]
            except (ValueError, IndexError):
                continue
            if neuron_count is None:
                neuron_count = len(zs)
            elif len(zs) != neuron_count:
                continue  # ragged row, skip
            np.asarray(zs, dtype=np.float16).tofile(out)
            frames += 1
        out.seek(4)
        out.write(struct.pack("<II", neuron_count or 0, frames))
    print(f"wrote {dst}: neuronCount={neuron_count}, frames={frames}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: firing_csv_to_f16.py <src.csv> <dst.f16>")
    main(sys.argv[1], sys.argv[2])
