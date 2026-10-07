#!/usr/bin/env python3
"""Summarize continuous client/server JSONL dumps without loading the capture into memory.

Optional --packets-dir extracts complete logical model/pose packets, with a manifest.
Packets are post-reassembly/application submissions, not UDP datagrams or retransmissions.
"""
import argparse
import glob
from collections import Counter, OrderedDict
import json
from pathlib import Path


def analyze(paths, packets_dir=None):
    counts, metrics, packet_bytes = Counter(), {}, Counter()
    pending = OrderedDict()
    malformed = incomplete = packets = sessions = 0
    losses = {}
    manifest = None
    if packets_dir:
        packets_dir.mkdir(parents=True, exist_ok=False)
        manifest = (packets_dir / "manifest.jsonl").open("w", encoding="utf-8")
    try:
        for path in paths:
            with path.open(encoding="utf-8") as stream:
                for line in stream:
                    try:
                        record = json.loads(line)
                        kind = record.get("event", record.get("kind", "unknown"))
                        counts[kind] += 1
                        if kind == "start":
                            sessions += 1
                            incomplete += len(pending)
                            pending.clear()
                        elif kind == "writer":
                            losses[str(sessions)] = {k: record.get(k, 0) for k in ("overrun_total", "write_errors_total")}
                        elif kind == "metric" or kind == "sample":
                            values = [record] if kind == "metric" else record.get("metrics", [])
                            for value in values:
                                name = value["name"]
                                entry = metrics.setdefault(name, {"count": 0, "sum": 0, "max": 0, "unit": value.get("unit", "ms")})
                                entry["count"] += value["count"]
                                entry["sum"] += value.get("sum_ms", value.get("sum", 0))
                                entry["max"] = max(entry["max"], value.get("max_ms", value.get("max", 0)))
                        elif kind == "packet":
                            if record["lane"] not in (3, 4) or not 0 < record["size"] <= 524288:
                                raise ValueError("unexpected packet envelope")
                            part = bytes.fromhex(record["hex"])
                            key = record["id"]
                            if key not in pending:
                                if len(pending) == 64:
                                    pending.popitem(last=False)
                                    incomplete += 1
                                pending[key] = (record, bytearray())
                            header, data = pending[key]
                            if record["offset"] != len(data) or record["size"] != header["size"] or record["lane"] != header["lane"] or record["outgoing"] != header["outgoing"] or len(data) + len(part) > header["size"]:
                                del pending[key]
                                incomplete += 1
                                continue
                            data.extend(part)
                            if len(data) == header["size"]:
                                packets += 1
                                direction = "tx" if header["outgoing"] else "rx"
                                packet_bytes[f"{direction}_lane_{header['lane']}"] += len(data)
                                if manifest:
                                    filename = f"{packets:09d}-{direction}-{header['lane']}.bin"
                                    (packets_dir / filename).write_bytes(data)
                                    manifest.write(json.dumps({k: v for k, v in header.items() if k != "hex"} | {"file": filename, "session": sessions}) + "\n")
                                del pending[key]
                    except (ValueError, KeyError, TypeError):
                        malformed += 1
        for metric in metrics.values():
            metric["mean"] = metric["sum"] / metric["count"] if metric["count"] else 0
        return {"files": len(paths), "events": dict(counts), "metrics": metrics, "complete_packets": packets,
                "logical_bytes": dict(packet_bytes), "incomplete_packets": incomplete + len(pending),
                "malformed_records": malformed, "writer_totals_by_session": losses}
    finally:
        if manifest:
            manifest.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("files", nargs="+", help="Trace parts, globs or session directory from ONE client/server")
    parser.add_argument("--packets-dir", type=Path, help="New output directory; complete packets only")
    args = parser.parse_args()
    def first_time(path):
        with path.open(encoding="utf-8") as stream:
            for line in stream:
                try:
                    return json.loads(line).get("utc_ms", 0)
                except ValueError:
                    continue
        return 0
    files = set()
    for item in args.files:
        path = Path(item)
        matches = list(path.rglob("trace-*.jsonl")) if path.is_dir() else [Path(x) for x in glob.glob(item)]
        if not matches:
            parser.error(f"No trace files: {item}")
        files.update(matches)
    print(json.dumps(analyze(sorted(files, key=first_time), args.packets_dir), indent=2))


if __name__ == "__main__":
    main()
