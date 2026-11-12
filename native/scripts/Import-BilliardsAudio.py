"""Prepare the CC0 recordings listed in BilliardsAudio-CC0.txt.

Development only: requires PyAV, NumPy and SciPy; not used at build/runtime.
Input directory: clack.mp3 (539854), shots.mp3 (350918), public HQ previews.
"""
import argparse
import hashlib
import json
from pathlib import Path

import av
import numpy as np
from scipy.signal import butter, resample_poly, sosfilt


def decode(path):
    chunks = []
    with av.open(str(path)) as container:
        for frame in container.decode(audio=0):
            data = frame.to_ndarray().astype(np.float64)
            if frame.format.is_planar:
                data = data.mean(axis=0)
            else:
                data = data.reshape(-1, frame.layout.nb_channels).mean(axis=1)
            if not frame.format.name.startswith("flt") and not frame.format.name.startswith("dbl"):
                data /= 32768.0
            chunks.append(data)
            rate = frame.sample_rate
    return np.concatenate(chunks), rate


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    sources = {name: decode(args.input / (name + ".mp3")) for name in ("clack", "shots")}
    # Keep the recorded attacks/tails; only trim, resample, remove DC, fade edges,
    # and normalize. The two feedback variants explicitly use a low-pass filter.
    cuts = [
        ("cue-soft", "shots", 7.073, 7.253, None),
        ("cue-hard", "shots", .244, .434, None),
        ("collision-a", "clack", 0, .239, None),
        ("collision-b", "shots", 3.910, 4.100, None),
        ("cushion", "shots", 7.578, 7.768, 1600),
        ("pocket", "shots", 13.627, 13.867, 2300),
    ]
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, source, start, end, lowpass in cuts:
        wave, rate = sources[source]
        clip = wave[round(start * rate):round(end * rate)].copy()
        clip -= np.mean(clip)
        onset = np.flatnonzero(np.abs(clip) > np.max(np.abs(clip)) * .045)
        lead = max(0, int(onset[0]) - round(rate * .001)) if len(onset) else 0
        clip = clip[lead:]
        if lowpass:
            clip = sosfilt(butter(3, lowpass, fs=rate, output="sos"), clip)
        # All samples use the same lightweight playback format.
        from math import gcd
        divisor = gcd(rate, 44100)
        clip = resample_poly(clip, 44100 // divisor, rate // divisor)
        clip[:44] *= np.linspace(0, 1, min(44, len(clip)))
        clip[-353:] *= np.linspace(1, 0, min(353, len(clip)))
        # Match loudness across takes so changing soft/hard samples cannot make
        # a stronger shot unexpectedly quieter; retain a 0.90 peak ceiling.
        clip *= min(.90 / max(.001, np.max(np.abs(clip))), .05 / max(.001, np.sqrt(np.mean(clip * clip))))
        pcm = np.rint(clip * 32767).astype("<i2").tobytes()
        path = args.output / (name + ".pcm")
        path.write_bytes(pcm)
        manifest.append({"file": path.name, "source": source, "startSeconds": start,
                         "endSeconds": end, "trimmedLeadSeconds": lead / rate,
                         "inputSha256": hashlib.sha256((args.input / (source + ".mp3")).read_bytes()).hexdigest(),
                         "lowpassHz": lowpass, "sampleRate": 44100,
                         "channels": 1, "bits": 16, "targetRms": .05, "bytes": len(pcm),
                         "sha256": hashlib.sha256(pcm).hexdigest()})
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
