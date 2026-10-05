"""Independent Python fixtures for the C++ diagnostic archive contract."""
import json
import struct
import tempfile
import unittest
from pathlib import Path

import phantom_benchmark as bench


def fixture(path):
    nodes = 4
    frames = []
    for at in (0.0, .05, .10):
        frame = struct.pack("<dB", at, int(at > 0))
        for i in range(nodes):
            frame += struct.pack("<17fBI", 1, 0, 0, 0, 1, 0, 0, 0, 1,
                                 i * 10 + at, 2, 3, 1, i * 10 + at, 2, 3, 4,
                                 0, bench.NONE if i == 0 else (2 if i == 3 and at else 0))
        frames.append(frame)
    # Construct independently of the benchmark's own packing functions.
    data = b"DSPPOSE1" + struct.pack("<4I", 1, nodes, len(frames), 20) + b"".join(frames)
    metadata = {"version": 1, "scenario": "equipment", "runtime": "fixture",
                "rate": 20, "frameCount": len(frames), "seconds": .10, "filePoseBytes": len(data),
                "sampleP95Ms": .2, "nodes": [
                    {"geometry": False, "excluded": False, "channel": "world"},
                    {"geometry": False, "excluded": False, "channel": "world"},
                    {"geometry": False, "excluded": False, "channel": "boneWorld"},
                    {"geometry": True, "excluded": False, "channel": "world"}],
                "skins": [{"geometry": 3, "root": 0, "bones": [2]}]}
    (path / "metadata.json").write_text(json.dumps(metadata), encoding="utf-8")
    (path / "poses.bin").write_bytes(data)
    (path / "appearance.nif").write_bytes(b"fixture, not an actual NIF" * 100)
    (path / "complete.txt").write_text("fixture", encoding="ascii")
    return metadata, frames


class BenchmarkTests(unittest.TestCase):
    def test_independent_layout_compaction_and_codec_roundtrips(self):
        with tempfile.TemporaryDirectory(prefix="DreamsleevePhantomBenchmark-") as directory:
            path = Path(directory)
            meta, frames = fixture(path)
            available, _ = bench.codecs()
            summary, rows = bench.benchmark(path, available, 1, 1/16)
            self.assertEqual(summary["selected_channels"], [0, 2, 3])
            self.assertEqual(summary["parent_changes"], 1)
            self.assertLessEqual(summary["errors"]["position_units_max"], .0542)
            self.assertLess(summary["errors"]["rotation_degrees_max"], .01)
            self.assertTrue(all(row["lossless_verified"] for row in rows))
            selected, origin, trs, quant, errors = bench.compact(meta, frames, 1/16)
            self.assertLess(len(quant[0]), len(trs[0]))
            packets = bench.delta_packets(quant, len(selected), bench.QUANT.size)
            self.assertEqual(list(bench.restore_deltas(packets, len(selected), bench.QUANT.size)), quant)
            self.assertEqual(packets[0][:1], b"K")
            self.assertEqual(packets[1][:1], b"D")

    def test_rotations_reconstruct_including_half_turns(self):
        for q in [(0, 0, 0, 1), (1, 0, 0, 0), (0, 1, 0, 0), (0, 0, 1, 0), (.5, .5, .5, .5)]:
            matrix = bench.matrix(q)
            restored = bench.matrix(bench.quaternion(matrix))
            self.assertLess(max(abs(a-b) for a,b in zip(matrix, restored)), 1e-6)

    def test_rejects_partial_truncated_and_nonfinite_archives(self):
        with tempfile.TemporaryDirectory(prefix="DreamsleevePhantomInvalid-") as directory:
            path = Path(directory)
            fixture(path)
            original = (path / "poses.bin").read_bytes()
            (path / "poses.bin").write_bytes(original[:-1])
            with self.assertRaisesRegex(ValueError, "truncated"):
                bench.read_archive(path)
            damaged = bytearray(original)
            struct.pack_into("<f", damaged, 24 + 9, float("nan"))
            (path / "poses.bin").write_bytes(damaged)
            with self.assertRaisesRegex(ValueError, "invalid pose"):
                bench.read_archive(path)
            (path / "complete.txt").unlink()
            with self.assertRaisesRegex(ValueError, "incomplete"):
                bench.read_archive(path)


if __name__ == "__main__":
    unittest.main()
