#!/usr/bin/env python3
"""Inspect a local diagnostic archive without game, server or extra Python packages.

Unquantized snapshots and production pre-Zstd bytes are stored explicitly. Models
and independent pose frames remain Zstd; --extract makes them available to codec
experiments. This tool never reads authentication packets or user configuration.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import statistics
import struct
from pathlib import Path

MAGIC = b"DLPDIAG1"
MAX_RECORD = 64 * 1024 * 1024 + 1024


def take(data: bytes, offset: int, fmt: str):
    s = struct.Struct("<" + fmt)
    if offset + s.size > len(data):
        raise ValueError("truncated record")
    values = s.unpack_from(data, offset)
    return values, offset + s.size


def protobuf(data: bytes) -> dict[int, int | bytes]:
    def varint(at: int):
        value = 0
        for shift in range(0, 70, 7):
            if at >= len(data):
                raise ValueError("truncated protobuf")
            b = data[at]
            at += 1
            value |= (b & 127) << shift
            if b < 128:
                return value, at
        raise ValueError("protobuf varint overflow")
    fields: dict[int, int | bytes] = {}
    at = 0
    while at < len(data):
        tag, at = varint(at)
        field, kind = tag >> 3, tag & 7
        if not field or field in fields:
            raise ValueError("invalid/duplicate protobuf field")
        if kind == 0:
            value, at = varint(at)
        elif kind == 2:
            n, at = varint(at)
            value = data[at:at + n]
            if len(value) != n:
                raise ValueError("truncated protobuf data")
            at += n
        else:
            raise ValueError("unsupported wire type in pose envelope")
        fields[field] = value
    return fields


def distribution(values: list[int | float]) -> dict:
    if not values:
        return {"count": 0}
    ordered = sorted(values)
    return {"count": len(values), "min": ordered[0], "mean": statistics.mean(values),
            "p50": ordered[int((len(values) - 1) * .5)], "p95": ordered[int((len(values) - 1) * .95)], "max": ordered[-1]}


def inspect(path: Path, extract: Path | None = None, compare_deformations: bool = False) -> dict:
    if path.is_dir():
        path = path / "capture.phdiag"
    models: dict[int, dict] = {}
    samples, encodes, sent, movements, failures = [], [], [], [], []
    total = 20
    previous_deformations: dict[tuple[int, int], bytes] = {}
    deformation_changes: dict[tuple[int, int], dict] = {}
    if extract:
        extract.mkdir(parents=True, exist_ok=True)
    with path.open("rb") as stream:
        header = stream.read(20)
        if len(header) != 20 or header[:8] != MAGIC:
            raise ValueError("not a phantom diagnostic archive")
        version, protocol, asset_version = struct.unpack_from("<III", header, 8)
        if version != 1 or asset_version != 1:
            raise ValueError("unsupported archive/model version")
        records = 0
        while h := stream.read(8):
            records += 1
            if len(h) != 8 or records > 4096:
                raise ValueError("truncated header or too many records")
            kind, n = struct.unpack("<II", h)
            if n > MAX_RECORD:
                raise ValueError("archive record exceeds limits")
            data = stream.read(n)
            if len(data) != n:
                raise ValueError("truncated record body (recording did not finish)")
            total += 8 + n
            if kind == 1:
                (gen, raw_n, compressed_n), at = take(data, 0, "QII")
                digest, model = data[at:at + 32], data[at + 32:]
                if len(model) != compressed_n or hashlib.sha256(model).digest() != digest or gen in models:
                    raise ValueError("invalid model size, digest or duplicate generation")
                models[gen] = {"rawBytes": raw_n, "compressedBytes": compressed_n, "sha256": digest.hex()}
                if extract:
                    (extract / f"model-{gen}.zst").write_bytes(model)
            elif kind == 2:
                (capture_ms, camera), at = take(data, 0, "dB")
                actor, at = take(data, at, "QQQffffff")
                sizes, at = take(data, at, "III")
                original_n, raw_n, compressed_n = sizes
                if at + sum(sizes) != len(data):
                    raise ValueError("invalid sample lengths")
                original = data[at:at + original_n]
                raw = data[at + original_n:at + original_n + raw_n]
                compressed = data[-compressed_n:] if compressed_n else b""
                pose, pos = take(original, 0, "QQQQfffIII")
                gen, seq, context, timestamp = pose[:4]
                channels, bounds, dynamic = pose[-3:]
                if gen not in models or not (1 <= channels <= 4096 and bounds <= 512 and dynamic <= bounds):
                    raise ValueError("sample precedes model or invalid counts")
                channel_start = pos
                pos += channels * 33 + bounds * 16
                dynamic_vertices = 0
                for _ in range(dynamic):
                    (geometry, count), pos = take(original, pos, "II")
                    if geometry >= bounds or count > 65535:
                        raise ValueError("invalid deformation")
                    dynamic_vertices += count
                    if compare_deformations:
                        key = (gen, geometry)
                        body = original[pos:pos + count * 24]
                        if len(body) != count * 24:
                            raise ValueError("truncated deformation")
                        entry = deformation_changes.setdefault(key, {
                            "vertices": count, "pairs": 0, "identicalPairs": 0,
                            "changedPositions": [], "changedNormals": []})
                        previous = previous_deformations.get(key)
                        if previous is not None:
                            if len(previous) != len(body):
                                raise ValueError("deformation count changed within generation")
                            entry["pairs"] += 1
                            same = previous == body
                            entry["identicalPairs"] += int(same)
                            for name, offset in (("changedPositions", 0), ("changedNormals", count * 12)):
                                a = struct.iter_unpack("<fff", body[offset:offset + count * 12])
                                b = struct.iter_unpack("<fff", previous[offset:offset + count * 12])
                                entry[name].append(0 if same else sum(x != y for x, y in zip(a, b)))
                        previous_deformations[key] = body
                    pos += count * 24
                if pos != len(original) or raw_n != 64 + channels * 19 + bounds * 10 + dynamic * 8 + dynamic_vertices * 24:
                    raise ValueError("snapshot body has wrong length")
                raw_header, _ = take(raw, 0, "IIQQQQfffIII")
                if raw_header[:2] != (0x50504C44, asset_version) or raw_header[2:] != pose:
                    raise ValueError("production and oracle snapshot headers differ")
                root, _ = take(original, channel_start, "ffffffffB")
                root_delta = math.dist(root[:3], actor[3:6])
                samples.append({"generation": gen, "sequence": seq, "context": context, "timeUs": timestamp,
                                "channels": channels, "bounds": bounds, "dynamicMeshes": dynamic, "dynamicVertices": dynamic_vertices,
                                "captureMs": capture_ms, "firstPerson": bool(camera), "originalBytes": original_n,
                                "rawBytes": raw_n, "compressedBytes": compressed_n, "rootActorDistance": root_delta,
                                "channelBytes": channels * 19, "boundBytes": bounds * 10,
                                "deformationBytes": dynamic * 8 + dynamic_vertices * 24})
                if extract:
                    stem = f"pose-{gen}-{seq}"
                    for suffix, body in (("original.bin", original), ("quantized.bin", raw), ("zst", compressed)):
                        (extract / f"{stem}.{suffix}").write_bytes(body)
            elif kind == 3:
                values, at = take(data, 0, "QQQQdQ")
                if at != len(data):
                    raise ValueError("invalid encoding record")
                encodes.append(dict(zip(("generation", "sequence", "context", "timeUs", "ms", "bytes"), values)))
            elif kind == 4:
                (when,), at = take(data, 0, "Q")
                packet = protobuf(data[at:])
                if packet.get(1) != protocol or not isinstance(packet.get(2), bytes):
                    raise ValueError("invalid client pose packet/version")
                pose_packet = protobuf(packet[2])
                payload = pose_packet.get(5)
                if not isinstance(payload, bytes):
                    raise ValueError("missing pose payload")
                sent.append({"timeUs": when, "sampledAtUs": pose_packet.get(4, 0), "generation": pose_packet.get(1, 0),
                             "sequence": pose_packet.get(3, 0), "context": pose_packet.get(2, 0),
                             "envelopeBytes": len(data) - 8, "payloadBytes": len(payload)})
                if extract:
                    (extract / f"sent-{pose_packet.get(1, 0)}-{pose_packet.get(3, 0)}.protobuf").write_bytes(data[at:])
            elif kind == 5:
                values, at = take(data, 0, "QQQQffffffQ")
                if at + values[-1] != len(data):
                    raise ValueError("invalid movement record")
                movements.append(dict(zip(("sentAtUs", "context", "sequence", "timeUs", "x", "y", "z", "rx", "ry", "rz", "bytes"), values)))
                if extract:
                    (extract / f"movement-{values[1]}-{values[2]}.protobuf").write_bytes(data[at:])
            elif kind == 6:
                (when, failure), at = take(data, 0, "QI")
                if at != len(data):
                    raise ValueError("invalid capture error")
                failures.append({"timeUs": when, "failure": failure})
            else:
                raise ValueError(f"unknown record {kind}")
    timestamps = [s["timeUs"] for s in samples]
    seconds = (max(timestamps) - min(timestamps)) / 1e6 if len(timestamps) > 1 else 0
    intervals = [(b - a) / 1000 for a, b in zip(timestamps, timestamps[1:]) if b > a]
    matched = {(s["generation"], s["sequence"]): s for s in samples}
    nearest = []
    times = sorted(m["timeUs"] for m in movements)
    if times:
        import bisect
        for s in samples:
            i = bisect.bisect_left(times, s["timeUs"])
            nearby = times[max(0, i - 1):i + 1]
            nearest.append(min(abs(t - s["timeUs"]) for t in nearby) / 1000)
    changes = {f"{gen}:{geometry}": {
        key: distribution(value) if isinstance(value, list) else value
        for key, value in entry.items()
    } for (gen, geometry), entry in deformation_changes.items()}
    return {"format": version, "protocol": protocol, "assetVersion": asset_version, "archiveBytes": total,
            "models": models, "samples": len(samples), "encoded": len(encodes), "acceptedByEnet": len(sent),
            "movementPackets": len(movements), "captureFailures": failures, "seconds": seconds,
            "capturedHz": (len(samples) - 1) / seconds if seconds else None,
            "sampleSequenceGaps": [
                {"generation": a["generation"], "after": a["sequence"], "next": b["sequence"],
                 "missing": b["sequence"] - a["sequence"] - 1, "intervalMs": (b["timeUs"] - a["timeUs"]) / 1000}
                for a, b in zip(samples, samples[1:])
                if a["generation"] == b["generation"] and b["sequence"] > a["sequence"] + 1],
            "deformationChanges": changes,
            "sampleIntervalMs": distribution(intervals), "captureMs": distribution([s["captureMs"] for s in samples]),
            "productionEncodeMs": distribution([e["ms"] for e in encodes]),
            "poseSizes": {k: distribution([s[k] for s in samples]) for k in (
                "originalBytes", "rawBytes", "compressedBytes", "channelBytes", "boundBytes", "deformationBytes", "dynamicVertices")},
            "posePacketBytes": distribution([s["envelopeBytes"] for s in sent]),
            "posePacketsWithOracle": sum((s["generation"], s["sequence"]) in matched for s in sent),
            "nearestMovementTimeMs": distribution(nearest),
            "rootActorDistance": distribution([s["rootActorDistance"] for s in samples]),
            "firstPersonSamples": sum(s["firstPerson"] for s in samples),
            "note": "ENet acceptance is not remote delivery; UDP/ENet fragment headers are not in these byte counts."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--extract", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--compare-deformations", action="store_true",
                        help="compare saved vertex positions/normals between successive samples of each generation")
    args = parser.parse_args()
    try:
        report = inspect(args.archive, args.extract, args.compare_deformations)
    except (OSError, ValueError, struct.error) as error:
        parser.exit(1, f"Invalid diagnostic archive: {error}\n")
    text = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    if args.output:
        args.output.write_text(text, encoding="utf-8")
    else:
        print(text)


if __name__ == "__main__":
    main()
