#!/usr/bin/env python3
"""Convert organoid spike data to the TFR1 float16 firing blob.

Input, either:
  t_spk_mat_*.npy      shape (units, samples), spike counts per 1 kHz sample; several recordings
                       concatenated along time (--segments). Envelope is built here (see below).
  *_timeseries.csv ... one lab timeseries CSV per recording, in playback order, with
                       column blocks t_spk_mat_*, rate_mat_*, rates_norm_* (1 kHz rows). The lab's
                       rates_norm (normalized per unit across all recordings) is used as-is,
                       only block-averaged to --fps; --tau-ms/--norm-pct/--gamma do not apply.
Output: <dst>.f16      TFR1 blob (same layout as firing_csv_to_f16.py: magic, uint32 neuronCount,
                       uint32 frameCount, frame-major float16), continuous 0..1 envelope
        <dst>.json     sidecar (or --sidecar): fps, segments (frame ranges), burst-onset frames, stats
        --positions    optional labels_positions.csv (header backbone_labels,xy_norm_0,xy_norm_1).
                       Without real electrode coordinates this is a SYNTHETIC functional layout:
                       classical MDS on 1-corr of baseline activity, fitted into the organoid
                       footprint of --mask (a PGM still from the imaging video).

The sims compare per-neuron values against thresholds tuned on organoid_firing.f16 (continuous,
right-skewed: median ~0.004, ~2% > 0.5). Spikes are therefore binned to --fps, convolved with an
exponential kernel (--tau-ms), scaled per neuron so its --norm-pct percentile maps to 1, then
raised to --gamma. Defaults reproduce the reference distribution (printed after each run).
All segments share one normalization so a quiet segment stays quiet.
"""
import argparse, json, os, struct
import numpy as np


def envelope(raster, src_hz, fps, tau_ms):
    n, t = raster.shape
    step = src_hz / fps
    edges = np.round(np.arange(0, t + 1e-9, step)).astype(np.int64)
    edges = edges[edges <= t]
    counts = np.add.reduceat(raster, edges[:-1], axis=1).astype(np.float32)
    decay = np.exp(-1000.0 / (fps * tau_ms))
    out = np.empty_like(counts)
    acc = np.zeros(n, np.float32)
    for f in range(counts.shape[1]):
        acc = acc * decay + counts[:, f]
        out[:, f] = acc
    return out


def load_timeseries(paths):
    """Concatenate lab timeseries CSVs -> (raster units x samples, rates_norm samples x units, segments)."""
    rasters, rates, segs, t = [], [], [], 0
    for p in paths:
        hdr = open(p).readline().strip().split(",")
        cols = lambda prefix: [i for i, h in enumerate(hdr) if h.rsplit("_", 1)[0] == prefix]
        a = np.loadtxt(p, delimiter=",", skiprows=1, dtype=np.float32)
        rasters.append(a[:, cols("t_spk_mat")].T)
        rates.append(a[:, cols("rates_norm")])
        name = os.path.basename(p).rsplit("_", 2)[-2]  # ..._baseline_timeseries.csv -> baseline
        segs.append((name, t, t + a.shape[0]))
        t += a.shape[0]
    return np.concatenate(rasters, 1), np.concatenate(rates, 0), segs


def block_mean(x, src_hz, fps):
    """Average samples (axis 0) into frames of src_hz / fps samples."""
    edges = np.round(np.arange(0, x.shape[0] + 1e-9, src_hz / fps)).astype(np.int64)
    edges = edges[edges <= x.shape[0]]
    return np.add.reduceat(x, edges[:-1], axis=0) / np.diff(edges)[:, None]


def burst_onsets(raster, src_hz, start, stop, ref_slice, sd=4.0, bin_ms=10, merge_ms=1500):
    """Population-rate threshold crossings; threshold from ref_slice so segments are comparable.
    merge_ms=1500 folds double-peaked bursts into one, matching the lab's count (20 in baseline)."""
    p = raster.sum(0).astype(np.float32)
    b = int(src_hz * bin_ms / 1000)

    def smooth(a):
        a = a[: a.size // b * b].reshape(-1, b).sum(1)
        return np.convolve(a, np.ones(5) / 5, "same")

    ref = smooth(p[ref_slice])
    thr = ref.mean() + sd * ref.std()
    s = smooth(p[start:stop])
    up = np.flatnonzero((s[1:] >= thr) & (s[:-1] < thr)) + 1
    ev = []
    for u in up:
        if not ev or u - ev[-1] > merge_ms / bin_ms:
            ev.append(u)
    return [start + e * b for e in ev], float(thr)


def mds_layout(raster, src_hz, ref_slice, mask_path):
    n = raster.shape[0]
    b = int(src_hz * 0.05)
    x = raster[:, ref_slice]
    x = x[:, : x.shape[1] // b * b].reshape(n, -1, b).sum(2).astype(np.float64)
    c = np.nan_to_num(np.corrcoef(x))
    d2 = (1.0 - c) ** 2
    j = np.eye(n) - 1.0 / n
    w, v = np.linalg.eigh(-0.5 * j @ d2 @ j)
    xy = v[:, -2:] * np.sqrt(np.maximum(w[-2:], 0))
    # Rank-radial remap fills a unit disk evenly while keeping angular neighbourhoods.
    xy -= xy.mean(0)
    r = np.hypot(xy[:, 0], xy[:, 1])
    ang = np.arctan2(xy[:, 1], xy[:, 0])
    rr = np.sqrt((np.argsort(np.argsort(r)) + 0.5) / n) * 0.92
    disk = np.stack([rr * np.cos(ang), rr * np.sin(ang)], 1)

    cx, cy, ax = 0.5, 0.5, np.array([[0.35, 0], [0, 0.35]])
    if mask_path:
        img = read_pgm(mask_path)
        k = 8  # block-average before thresholding: the organoid edge is speckled
        h, wd = img.shape[0] // k, img.shape[1] // k
        img = img[: h * k, : wd * k].reshape(h, k, wd, k).mean((1, 3))
        m = img > 0.5 * (np.percentile(img, 5) + np.percentile(img, 99))
        yy, xx = np.nonzero(m)
        pts = np.stack([xx / wd, yy / h], 1)
        cx, cy = pts.mean(0)
        cov = np.cov((pts - [cx, cy]).T)
        ew, ev = np.linalg.eigh(cov)
        ax = ev * (2.0 * np.sqrt(ew))  # 2 sd ~ edge of a uniform disk
        cx, cy = 0.5, 0.5  # keep the footprint's shape, drop the microscope's framing offset
    uv = np.array([cx, cy]) + disk @ ax.T
    uv = np.clip(uv, 0.0, 1.0)
    uv[:, 1] = 1.0 - uv[:, 1]  # image rows go down; CSV y goes up (sims y-flip on parse)
    return uv


def xy_layout(path, n):
    """Unit coordinates -> 0..1, one scale for both axes so the electrode array keeps its aspect."""
    xy = (np.load(path) if path.endswith(".npy") else np.loadtxt(path, delimiter=",", ndmin=2))[:, :2]
    xy = xy.astype(np.float64)
    assert xy.shape[0] == n, f"--xy has {xy.shape[0]} rows, expected {n}"
    lo, hi = xy.min(0), xy.max(0)
    # No y-flip: the lab's dataprocessing.py writes Kilosort y straight into xy_norm_1, which is
    # the convention NeuronFiringSource parses. Differs from that script only in keeping aspect.
    return (xy - (lo + hi) / 2) / max((hi - lo).max(), 1e-9) * 0.9 + 0.5


def read_pgm(path):
    """Binary 8-bit PGM (P5), e.g. `ffmpeg -ss 60 -i baseline.avi -frames:v 1 -pix_fmt gray mask.pgm`."""
    data = open(path, "rb").read()
    head = data.split(maxsplit=4)
    assert head[0] == b"P5", "expected binary PGM (P5)"
    w, h = int(head[1]), int(head[2])
    return np.frombuffer(data[-w * h:], np.uint8).reshape(h, w).astype(np.float32)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("src", nargs="+", help="one .npy raster, or timeseries CSVs in playback order")
    ap.add_argument("dst", help="output .f16 path (sidecar written next to it as .json)")
    ap.add_argument("--src-hz", type=float, default=1000.0)
    ap.add_argument("--fps", type=float, default=60.0)
    ap.add_argument("--tau-ms", type=float, default=400.0)
    ap.add_argument("--norm-pct", type=float, default=99.5)
    ap.add_argument("--gamma", type=float, default=2.5,
                    help="contrast curve after normalizing; pushes lone spikes toward 0 like the reference blob")
    ap.add_argument("--segments", default="baseline:0:300000,ketamine:300111:-1",
                    help=".npy only: name:startSample:endSample,... (-1 = end). First is the burst-threshold "
                         "reference. Default matches t_spk_mat_filtered.npy against the lab's per-recording CSVs.")
    ap.add_argument("--blank", default="300000:300111",
                    help=".npy only: start:end sample windows to zero (the gap between recordings holds a "
                         "24-units-in-1-ms artifact), comma-separated")
    ap.add_argument("--labels", help="backbone_labels CSV (one 0/1 per unit) copied into --positions")
    ap.add_argument("--sidecar", help="sidecar .json path (default: next to dst)")
    ap.add_argument("--positions", help="write labels_positions.csv here (synthetic unless --xy is given)")
    ap.add_argument("--xy", help="real unit coordinates: .npy or .csv, units x >=2 (e.g. from Kilosort), same "
                                 "unit order as the spike data. Aspect-preserving fit, centred, 0.05 margin.")
    ap.add_argument("--mask", help="PGM still of the organoid to fit synthetic positions into")
    a = ap.parse_args()

    from_csv = a.src[0].endswith(".csv")
    if from_csv:
        raster, rates, segs = load_timeseries(a.src)
        n, t = raster.shape
        env = np.clip(block_mean(rates, a.src_hz, a.fps), 0, 1).astype(np.float16)  # frames x neurons
    else:
        raster = np.load(a.src[0]).astype(np.float32)
        n, t = raster.shape
        for w in filter(None, a.blank.split(",")):
            s0, s1 = map(int, w.split(":"))
            raster[:, s0:s1] = 0
        segs = []
        for spec in a.segments.split(","):
            name, s0, s1 = spec.split(":")
            s0, s1 = int(s0), (t if int(s1) < 0 else int(s1))
            segs.append((name, s0, s1))
        env = envelope(raster, a.src_hz, a.fps, a.tau_ms)
        scale = np.percentile(env, a.norm_pct, axis=1, keepdims=True)
        env = (np.clip(env / np.maximum(scale, 1e-6), 0, 1) ** a.gamma).T.astype(np.float16)
    ref = slice(segs[0][1], segs[0][2])
    frames = env.shape[0]

    with open(a.dst, "wb") as f:
        f.write(b"TFR1")
        f.write(struct.pack("<II", n, frames))
        env.tofile(f)

    to_frame = lambda s: int(round(s * a.fps / a.src_hz))
    e32 = env.astype(np.float32)
    side = {"source": [os.path.basename(p) for p in a.src], "srcHz": a.src_hz, "fps": a.fps,
            "envelope": "rates_norm (lab), block-averaged" if from_csv else
                        {"tauMs": a.tau_ms, "normPct": a.norm_pct, "gamma": a.gamma, "blankSamples": a.blank},
            "neuronCount": n, "frameCount": frames, "segments": []}
    for name, s0, s1 in segs:
        on, thr = burst_onsets(raster, a.src_hz, s0, s1, ref)
        f0, f1 = to_frame(s0), min(to_frame(s1), frames) - 1
        seg = e32[f0:f1 + 1]
        side["segments"].append({
            "name": name, "startFrame": f0, "endFrame": f1,
            "rateHzPerUnit": float(raster[:, s0:s1].sum() / n / ((s1 - s0) / a.src_hz)),
            "burstThreshold": thr, "burstOnsetFrames": [to_frame(o) for o in on],
            "envMean": float(seg.mean()), "frameMeanMax": float(seg.mean(1).max()),
        })
    json.dump(side, open(a.sidecar or a.dst.rsplit(".", 1)[0] + ".json", "w"), indent=1)

    x = e32[:, 0] - e32[:, 0].mean()
    lag = lambda L: float((x[:-L] * x[L:]).mean() / x.var())
    print(f"wrote {a.dst}: neurons={n} frames={frames} ({frames / a.fps:.0f}s @ {a.fps:g}fps)")
    print(f"  median={np.median(e32):.4f} mean={e32.mean():.4f} q90={np.quantile(e32, .9):.3f} "
          f"q99={np.quantile(e32, .99):.3f} frac>0.5={(e32 > .5).mean():.3f} "
          f"frameMeanMax={e32.mean(1).max():.3f} lag20={lag(20):.2f}")
    print("  ref organoid_firing.f16: median=0.0041 mean=0.0413 q90=0.097 q99=0.722 frac>0.5=0.020 "
          "frameMeanMax=0.209 lag20=0.40")
    for s in side["segments"]:
        print(f"  {s['name']:10s} frames {s['startFrame']}..{s['endFrame']} rate={s['rateHzPerUnit']:.2f}Hz "
              f"bursts={len(s['burstOnsetFrames'])} envMean={s['envMean']:.4f} frameMeanMax={s['frameMeanMax']:.3f}")

    if a.positions:
        uv = xy_layout(a.xy, n) if a.xy else mds_layout(raster, a.src_hz, ref, a.mask)
        labels = np.loadtxt(a.labels, skiprows=1, ndmin=1) if a.labels else np.zeros(n)
        assert labels.size == n, f"--labels has {labels.size} rows, expected {n}"
        with open(a.positions, "w") as f:
            f.write("backbone_labels,xy_norm_0,xy_norm_1\n")
            for b, (u, v) in zip(labels, uv):
                f.write(f"{b:.1f},{u:.7f},{v:.7f}\n")
        kind = f"positions from {os.path.basename(a.xy)}" if a.xy else "SYNTHETIC positions (MDS on baseline correlation)"
        print(f"wrote {a.positions}: {n} {kind}")


if __name__ == "__main__":
    main()
