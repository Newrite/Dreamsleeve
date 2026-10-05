#!/usr/bin/env python3
"""Read local diagnostic archives, measure codecs and pose representations.

No game, NiStream parser, server, or network is involved. Inputs are read-only.
Optional codecs: pip install -r Scripts/phantom-benchmark-requirements.txt
"""
from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import json
import math
import statistics
import struct
import time
from pathlib import Path

HEADER = struct.Struct("<8s4I")
FRAME = struct.Struct("<dB")
POSE = struct.Struct("<17fBI")
TRS = struct.Struct("<8fB")
QUANT = struct.Struct("<3i4hHB")
NONE = 0xFFFFFFFF


def read_archive(path: Path):
    if not (path / "complete.txt").is_file():
        raise ValueError(f"{path}: archive is incomplete")
    if (path / "metadata.json").stat().st_size > 16 * 1024 * 1024:
        raise ValueError("metadata exceeds 16 MiB")
    meta = json.loads((path / "metadata.json").read_text(encoding="utf-8"))
    for filename, limit in (("appearance.nif", 64), ("poses.bin", 160)):
        if (path / filename).stat().st_size > limit * 1024 * 1024:
            raise ValueError(f"{filename} exceeds {limit} MiB")
    data = (path / "poses.bin").read_bytes()
    if len(data) < HEADER.size:
        raise ValueError("truncated header")
    magic, version, nodes, count, rate = HEADER.unpack_from(data)
    if magic != b"DSPPOSE1" or version != 1 or not 1 <= nodes <= 4096 or not 2 <= count <= 1001 or rate not in (20, 40):
        raise ValueError("invalid pose header")
    stride = FRAME.size + nodes * POSE.size
    if len(data) != HEADER.size + count * stride:
        raise ValueError("truncated or trailing pose bytes")
    if (meta["version"], len(meta["nodes"]), meta["frameCount"], meta["rate"], meta["filePoseBytes"]) != (version, nodes, count, rate, len(data)):
        raise ValueError("metadata/header mismatch")
    frames = [data[HEADER.size + i * stride:HEADER.size + (i + 1) * stride] for i in range(count)]
    previous = -1.0
    for frame in frames:
        at, flags = FRAME.unpack_from(frame)
        if not math.isfinite(at) or at <= previous or flags & ~3:
            raise ValueError("non-monotonic time or unknown frame flags")
        previous = at
        for values in POSE.iter_unpack(frame[FRAME.size:]):
            if not all(map(math.isfinite, values[:17])) or values[17] > 1 or (values[18] != NONE and values[18] >= nodes):
                raise ValueError("invalid pose")
    return meta, data, frames, (path / "appearance.nif").read_bytes()


def quaternion(m):
    trace = m[0] + m[4] + m[8]
    if trace > 0:
        s = math.sqrt(max(0, trace + 1)) * 2
        q = ((m[7] - m[5]) / s, (m[2] - m[6]) / s, (m[3] - m[1]) / s, s / 4)
    else:
        i = max((0, 1, 2), key=lambda j: m[j * 3 + j])
        j, k = (i + 1) % 3, (i + 2) % 3
        s = math.sqrt(max(0, 1 + m[i * 3 + i] - m[j * 3 + j] - m[k * 3 + k])) * 2
        if s < 1e-12:
            raise ValueError("degenerate rotation matrix")
        a = [0.0] * 4
        a[i] = s / 4
        a[j] = (m[j * 3 + i] + m[i * 3 + j]) / s
        a[k] = (m[k * 3 + i] + m[i * 3 + k]) / s
        a[3] = (m[k * 3 + j] - m[j * 3 + k]) / s
        q = a
    norm = math.sqrt(sum(x * x for x in q))
    return tuple(x / norm for x in q)


def matrix(q):
    x, y, z, w = q
    return (1 - 2*(y*y + z*z), 2*(x*y - z*w), 2*(x*z + y*w),
            2*(x*y + z*w), 1 - 2*(x*x + z*z), 2*(y*z - x*w),
            2*(x*z - y*w), 2*(y*z + x*w), 1 - 2*(x*x + y*y))


def required_channels(meta):
    # A measured candidate: renderable geometry, all its skin bones/roots and
    # the recording root. No claim that omitted containers/bounds are safe in-game.
    selected = {0} | {i for i, n in enumerate(meta["nodes"]) if n["geometry"] and not n["excluded"]}
    for skin in meta["skins"]:
        if skin["geometry"] in selected:
            selected.update(skin["bones"])
            if skin["root"] != NONE:
                selected.add(skin["root"])
    if not all(0 <= i < len(meta["nodes"]) for i in selected):
        raise ValueError("invalid skin channel")
    return sorted(selected)


def compact(meta, frames, step):
    selected = required_channels(meta)
    origin = POSE.unpack_from(frames[0], FRAME.size)[9:12]
    raw, quant = [], []
    error = {"position_units_max": 0.0, "rotation_degrees_max": 0.0, "scale_max": 0.0, "matrix_element_max": 0.0}
    for frame in frames:
        full, small = bytearray(frame[:FRAME.size]), bytearray(frame[:FRAME.size])
        for i in selected:
            p = POSE.unpack_from(frame, FRAME.size + i * POSE.size)
            q = quaternion(p[:9])
            if q[3] < 0:  # Canonical sign for compression and byte deltas.
                q = tuple(-x for x in q)
            full.extend(TRS.pack(*p[9:12], *q, p[12], p[17]))
            xyz = tuple(round((p[j+9] - origin[j]) / step) for j in range(3))
            iq = tuple(round(x * 32767) for x in q)
            scale = round(p[12] * 1024)
            if not all(-(2**31) <= x < 2**31 for x in xyz) or not 0 <= scale <= 65535:
                raise ValueError("quantization range exceeded; no saturation permitted")
            small.extend(QUANT.pack(*xyz, *iq, scale, p[17]))
            restored = tuple(x / 32767 for x in iq)
            norm = math.sqrt(sum(x*x for x in restored))
            restored = tuple(x / norm for x in restored)
            dot = min(1.0, abs(sum(a*b for a, b in zip(q, restored))))
            error["rotation_degrees_max"] = max(error["rotation_degrees_max"], math.degrees(2 * math.acos(dot)))
            error["position_units_max"] = max(error["position_units_max"], math.sqrt(sum((origin[j] + xyz[j]*step - p[9+j])**2 for j in range(3))))
            error["scale_max"] = max(error["scale_max"], abs(scale/1024 - p[12]))
            error["matrix_element_max"] = max(error["matrix_element_max"], max(abs(a-b) for a,b in zip(p[:9], matrix(q))))
        raw.append(bytes(full))
        quant.append(bytes(small))
    return selected, origin, raw, quant, error


def delta_packets(frames, nodes, record_size, key_seconds=1.0):
    packets, previous, last_key = [], None, -math.inf
    mask_size = (nodes + 7) // 8
    for frame in frames:
        at = FRAME.unpack_from(frame)[0]
        if previous is None or at - last_key >= key_seconds:
            packets.append(b"K" + frame)
            last_key = at
        else:
            mask, changes = bytearray(mask_size), bytearray()
            for i in range(nodes):
                start = FRAME.size + i * record_size
                value = frame[start:start + record_size]
                if value != previous[start:start + record_size]:
                    mask[i // 8] |= 1 << (i % 8)
                    changes.extend(value)
            packets.append(b"D" + frame[:FRAME.size] + mask + changes)
        previous = frame
    return packets


def restore_deltas(packets, nodes, record_size):
    previous = None
    mask_size = (nodes + 7) // 8
    for packet in packets:
        if packet[0:1] == b"K":
            previous = packet[1:]
        else:
            if previous is None:
                raise ValueError("delta has no baseline")
            current = bytearray(packet[1:1+FRAME.size] + previous[FRAME.size:])
            mask = packet[1+FRAME.size:1+FRAME.size+mask_size]
            offset = 1+FRAME.size+mask_size
            for i in range(nodes):
                if mask[i//8] & (1 << (i%8)):
                    start = FRAME.size + i*record_size
                    current[start:start+record_size] = packet[offset:offset+record_size]
                    offset += record_size
            if offset != len(packet):
                raise ValueError("delta length mismatch")
            previous = bytes(current)
        yield previous


def codecs():
    available = [("none", lambda b: b, lambda b: b),
                 ("gzip-1", lambda b: gzip.compress(b, compresslevel=1, mtime=0), gzip.decompress),
                 ("gzip-6", lambda b: gzip.compress(b, compresslevel=6, mtime=0), gzip.decompress)]
    missing = []
    try:
        import lz4.frame
        available.append(("lz4", lambda b: lz4.frame.compress(b, block_linked=False), lz4.frame.decompress))
    except ImportError:
        missing.append("lz4")
    try:
        import zstandard as zstd
        for level in (1, 3, 6):
            c, d = zstd.ZstdCompressor(level=level), zstd.ZstdDecompressor()
            available.append((f"zstd-{level}", c.compress, d.decompress))
    except ImportError:
        missing.append("zstandard")
    return available, missing


def percentile(values, p):
    return sorted(values)[max(0, math.ceil(len(values) * p) - 1)]


def measure(pieces, codec, repeats, seconds):
    name, encode, decode = codec
    enc, dec, total = [], [], 0
    for iteration in range(repeats):
        total = 0
        for piece in pieces:
            start = time.perf_counter_ns()
            packed = encode(piece)
            enc.append((time.perf_counter_ns() - start) / 1e6)
            start = time.perf_counter_ns()
            restored = decode(packed)
            dec.append((time.perf_counter_ns() - start) / 1e6)
            if restored != piece:
                raise ValueError(f"{name}: decompression mismatch")
            total += len(packed)
    raw = sum(map(len, pieces))
    return {"codec": name, "raw_bytes": raw, "compressed_bytes": total, "ratio": total / raw,
            "bytes_per_second": total / seconds if seconds else None,
            "encode_median_ms": statistics.median(enc), "encode_p95_ms": percentile(enc, .95),
            "decode_median_ms": statistics.median(dec), "decode_p95_ms": percentile(dec, .95),
            "encode_total_ms": sum(enc)/repeats, "decode_total_ms": sum(dec)/repeats,
            "encode_peak_piece_bytes": max(map(len, pieces)), "lossless_verified": True}


def benchmark(path, available, repeats, step):
    meta, data, frames, appearance = read_archive(path)
    selected, origin, trs, quant, errors = compact(meta, frames, step)
    variants = {"canonical": (frames, len(meta["nodes"]), POSE.size),
                "selected-trs": (trs, len(selected), TRS.size), "selected-quantized": (quant, len(selected), QUANT.size)}
    rows = []
    def add(label, pieces, seconds=meta["seconds"]):
        for codec in available:
            rows.append({"capture": path.name, "variant": label, **measure(pieces, codec, repeats, seconds)})
    add("appearance", [appearance], 0)
    add("canonical-whole-file", [data])  # Archival upper bound, not a live streaming measurement.
    for name, (pieces, count, stride) in variants.items():
        add(name + "-snapshots", pieces)
        add(name + "-4-frame-blocks", [b"".join(pieces[i:i+4]) for i in range(0, len(pieces), 4)])
        deltas = delta_packets(pieces, count, stride)
        if list(restore_deltas(deltas, count, stride)) != pieces:
            raise ValueError("delta reconstruction mismatch")
        add(name + "-delta-1s", deltas)
    parents_changed = sum(POSE.unpack_from(b, FRAME.size+i*POSE.size)[18] != POSE.unpack_from(a, FRAME.size+i*POSE.size)[18]
                          for a,b in zip(frames, frames[1:]) for i in range(len(meta["nodes"])))
    details = {"capture": path.name, "scenario": meta["scenario"], "runtime": meta["runtime"],
               "frames": len(frames), "rate": meta["rate"], "seconds": meta["seconds"],
               "node_count": len(meta["nodes"]), "selected_channels": selected, "quantization_origin": origin,
               "position_step_units": step, "errors": errors, "parent_changes": parents_changed,
               "appearance_sha256": hashlib.sha256(appearance).hexdigest(), "poses_sha256": hashlib.sha256(data).hexdigest(),
               "sample_p95_ms": meta["sampleP95Ms"],
               "interval_p95_ms": percentile([(FRAME.unpack_from(b)[0]-FRAME.unpack_from(a)[0])*1000 for a,b in zip(frames, frames[1:])], .95),
               "channel_kinds": {kind: sum(n["channel"] == kind for n in meta["nodes"]) for kind in {n["channel"] for n in meta["nodes"]}}}
    return details, rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path, help="one completed archive or its parent directory")
    parser.add_argument("--output", type=Path, default=Path("build/phantom-benchmark"))
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--position-step", type=float, default=1/16)
    parser.add_argument("--require-codecs", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.repeats <= 20 or not math.isfinite(args.position_step) or not 0 < args.position_step <= 1:
        parser.error("invalid repeats or position step")
    available, missing = codecs()
    if args.require_codecs and missing:
        parser.error("missing codecs: " + ", ".join(missing))
    paths = [args.captures] if (args.captures / "complete.txt").is_file() else sorted(p.parent for p in args.captures.glob("*/complete.txt"))
    if not paths:
        parser.error("no completed captures found")
    summaries, rows = [], []
    for path in paths:
        print(f"Benchmarking {path.name}", flush=True)
        summary, measured = benchmark(path, available, args.repeats, args.position_step)
        summaries.append(summary)
        rows.extend(measured)
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"missing_codecs": missing, "repeats": args.repeats, "captures": summaries, "measurements": rows,
              "notes": ["Measured bytes exclude ENet/UDP headers and fragmentation.",
                        "Whole-file poses are an archival upper bound; 4-frame blocks add buffering latency.",
                        "Independent compression frames; delta variants still require the previous delivered pose, with full keys about every second.",
                        "Selected/quantized variants omit bounds and containers and require a separate visual replay test before adoption.",
                        "Timing is offline CPU time, not Skyrim frame time; peak-piece bytes is input size, not process peak RSS."]}
    (args.output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    with (args.output / "measurements.csv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"Results: {args.output.resolve()} ({len(paths)} captures, {len(rows)} measurements)")


if __name__ == "__main__":
    main()
