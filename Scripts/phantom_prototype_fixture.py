#!/usr/bin/env python3
"""Read-only SSE prototype archive audit; export local native-test fixtures.

The narrow NIF reader supports the stream-100 blocks emitted by our prototype.
It never loads an engine factory, follows texture paths or copies textures.
This covers saved material selection and vertex bytes, not live GPU resources.
"""
import argparse
import collections
import json
import math
from pathlib import Path
import struct


class Reader:
    def __init__(self, data):
        self.data, self.at = data, 0

    def take(self, size):
        if size < 0 or self.at + size > len(self.data):
            raise ValueError("truncated archive")
        value = self.data[self.at:self.at + size]
        self.at += size
        return value

    def read(self, fmt):
        result = struct.unpack('<' + fmt, self.take(struct.calcsize('<' + fmt)))
        return result[0] if len(result) == 1 else result

    def text(self, count='I'):
        return self.take(self.read(count)).decode('utf-8').rstrip('\0')


def nif(path):
    if path.stat().st_size > 128 * 1024**2:
        raise ValueError("oversized NIF")
    data = path.read_bytes()
    r = Reader(data[data.index(b'\n') + 1:])
    version, endian, user, count, stream = r.read('IBIII')
    if (version, endian, user, stream) != (0x14020007, 1, 12, 100) or count > 65535:
        raise ValueError("expected SSE prototype NIF")
    for _ in range(3):
        r.text('B')
    types = [r.text() for _ in range(r.read('H'))]
    ids = [r.read('H') for _ in range(count)]
    sizes = [r.read('I') for _ in range(count)]
    strings, _ = r.read('II')
    names = [r.text() for _ in range(strings)]
    for _ in range(r.read('I')):
        r.read('I')
    return names, [(types[i], r.take(size)) for i, size in zip(ids, sizes)]


def inspect(directory, output):
    meta_path = directory / 'metadata.json'
    if meta_path.stat().st_size > 16 * 1024**2:
        raise ValueError('oversized metadata')
    metadata = json.loads(meta_path.read_text(encoding='utf-8-sig'))
    names, blocks = nif(directory / 'appearance.nif')
    expected = collections.defaultdict(collections.deque)
    for node in metadata['nodes']:
        if node['geometry']:
            expected[node['name']].append(node['excluded'])
    surfaces, vertices, overrides = [], 0, 0
    for block_id, (kind, data) in enumerate(blocks):
        if kind not in ('BSTriShape', 'BSDynamicTriShape', 'BSSubIndexTriShape'):
            continue
        r = Reader(data)
        name, extras = r.read('II')
        name = names[name]
        r.take(extras * 4 + 4 + 4 + 52 + 4 + 16)
        skin, shader, alpha = r.read('III')
        descriptor, triangles, count, size = r.read('QHHI')
        packed = r.take(size)
        stride, partition = (descriptor & 15) * 4, False
        record = dict(name=name, effectMaterial=False, decalMaterial=False,
                      skinned=skin != 0xffffffff, dedicatedDecal=False,
                      hasShader=shader != 0xffffffff, shaderAlpha=1.0,
                      materialAlpha=1.0, excluded=expected[name].popleft())
        if shader != 0xffffffff:
            shader_kind, material = blocks[shader]
            record['effectMaterial'] = shader_kind == 'BSEffectShaderProperty'
            if shader_kind == 'BSLightingShaderProperty':
                m = Reader(material)
                _, _, extra_count = m.read('III')
                m.take(extra_count * 4 + 4)
                flags1, _ = m.read('II')
                record['skinned'] |= bool(flags1 & 2)
                record['decalMaterial'] = bool(flags1 & ((1 << 26) | (1 << 27)))
                record['dedicatedDecal'] = bool(flags1 & (1 << 24))
                m.take(16 + 4 + 16 + 4)
                record['materialAlpha'] = m.read('f')
        surfaces.append(record)
        # Selection oracle is the prototype's saved exclusion bit. Keep all
        # retained geometry's streams, including meshes currently hidden.
        if record['excluded']:
            continue
        if not size and skin != 0xffffffff:
            _, skin_data = blocks[skin]
            _, partition_id = struct.unpack_from('<II', skin_data)
            partition_kind, partition_data = blocks[partition_id]
            if partition_kind != 'NiSkinPartition':
                raise ValueError('unexpected skin partition')
            pr = Reader(partition_data)
            _, size, stride, descriptor = pr.read('IIIQ')
            packed = pr.take(size)
            if not stride or size % stride:
                raise ValueError('invalid partition stream')
            count, partition = size // stride, True
        if not count or not stride or len(packed) < count * stride:
            raise ValueError(f'missing vertex stream: {name}')
        if not descriptor & (1 << 44):
            # Face dynamic positions are separate in the archive. Do not
            # pretend the packed stream validates their live capture.
            overrides += 1
            continue
        # These engine-written SSE archives store present positions as FP32
        # even in skin partitions lacking FULLPREC. Face/head packed streams
        # with no positions were handled separately above. Reject a different
        # position footprint instead of guessing precision from finite values.
        offsets = [(descriptor >> (4 * a + 2)) & 0x3c for a in range(1, 9)
                   if descriptor & (1 << (44 + a))]
        if min([stride] + offsets) != 16:
            raise ValueError(f'unsupported archived position footprint: {name}')
        oracle = bytearray()
        for i in range(count):
            xyz = struct.unpack_from('<3f', packed, i * stride)
            if not all(map(math.isfinite, xyz)):
                raise ValueError(f'nonfinite archived position: {name}/{i}')
            oracle.extend(struct.pack('<3f', *xyz))
        payload = struct.pack('<8sQII', b'DLPVTX01', descriptor, stride, count)
        payload += packed[:count * stride] + oracle
        (output / f'{directory.name}-{block_id}.bin').write_bytes(payload)
        vertices += count
    if any(expected.values()):
        raise ValueError('metadata/NIF geometry mismatch')
    (output / f'{directory.name}.json').write_text(json.dumps(surfaces, ensure_ascii=False), encoding='utf-8')
    return dict(archive=directory.name, scenario=metadata['scenario'], frames=metadata['frameCount'],
                surfaces=len(surfaces), excluded=sum(x['excluded'] for x in surfaces),
                vertexPositions=vertices, separateDynamicStreams=overrides)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archives', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.resolve().is_relative_to(args.archives.resolve()):
        parser.error('output must be outside the source archives')
    args.output.mkdir(parents=True, exist_ok=False)
    report = [inspect(p, args.output) for p in sorted(args.archives.iterdir())
              if p.is_dir() and (p / 'complete.txt').is_file()]
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
