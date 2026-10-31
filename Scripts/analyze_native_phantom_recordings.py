#!/usr/bin/env python3
"""Read-only native diagnostics measurements; experimental codecs are NOT wire formats.

Requires numpy and a local zstd bridge built from the project's Zstd 1.5.7:
  clang -shared -O2 Scripts/phantom_zstd_probe.c -I <include> <zstd_static.lib> -o <probe.dll>
Outputs derived JSON/NIF files only into a NEW directory outside the archive tree.
No executable game objects are loaded. Originals are never modified.
"""
import argparse
import collections
import ctypes as C
import hashlib
import json
from pathlib import Path
import struct
import time

import numpy as np

from inspect_phantom_diagnostics import distribution, inspect, take
from phantom_prototype_fixture import nif


class Zstd:
    def __init__(self, path):
        self.lib = C.CDLL(str(path.resolve()))
        common = [C.c_void_p, C.c_size_t, C.c_void_p, C.c_size_t]
        for name, args in [('compress', common + [C.c_int]), ('decompress', common),
                           ('compress_dict', common + [C.c_void_p, C.c_size_t, C.c_int]),
                           ('decompress_dict', common + [C.c_void_p, C.c_size_t])]:
            f = getattr(self.lib, name)
            f.argtypes, f.restype = args, C.c_size_t
        self.lib.is_error.argtypes, self.lib.is_error.restype = [C.c_size_t], C.c_uint

    def compress(self, data, level=1, dictionary=None):
        out = C.create_string_buffer(len(data) + len(data)//128 + 1024)
        if dictionary is None:
            n = self.lib.compress(out, len(out), data, len(data), level)
        else:
            n = self.lib.compress_dict(out, len(out), data, len(data), dictionary, len(dictionary), level)
        if self.lib.is_error(n):
            raise ValueError('Zstd compression failed')
        return out.raw[:n]

    def decompress(self, data, size, dictionary=None):
        out = C.create_string_buffer(size)
        if dictionary is None:
            n = self.lib.decompress(out, size, data, len(data))
        else:
            n = self.lib.decompress_dict(out, size, data, len(data), dictionary, len(dictionary))
        if self.lib.is_error(n) or n != size:
            raise ValueError('Zstd decompression failed')
        return out.raw


def records(path):
    with path.open('rb') as f:
        header=f.read(20)
        assert header[:8] == b'DLPDIAG2' and struct.unpack('<III',header[8:]) in ((2,22,2),(2,23,2))
        while header := f.read(8):
            kind, length = struct.unpack('<II', header)
            data = f.read(length)
            assert len(data) == length
            yield kind, data


def varint(n):
    return max(1, (n.bit_length() + 6)//7)


def packet_size(payload, gen, seq, context, timestamp, server=False):
    inner = sum(1 + varint(n) for n in (gen, context, seq, timestamp) if n)
    inner += 1 + varint(payload) + payload
    # Server projection: player_id=512, view_revision=1. No transport headers.
    return 2 + (5 if server else 0) + 1 + varint(inner) + inner


def shuffle(data, count, stride):
    arr = np.frombuffer(data, dtype=np.uint8).reshape(count, stride)
    result = arr.T.copy().tobytes()
    assert np.frombuffer(result, dtype=np.uint8).reshape(stride, count).T.copy().tobytes() == data
    return result


def variants(raw, channels, bounds):
    channel_data, bound_data = raw[60:60+23*channels], raw[60+23*channels:]
    planar = raw[:60] + shuffle(channel_data, channels, 23) + shuffle(bound_data, bounds, 16)
    small_channels, small_bounds = bytearray(), bytearray()
    for data, count, stride, out in [(channel_data, channels, 23, small_channels),
                                      (bound_data, bounds, 16, small_bounds)]:
        for i in range(count):
            row = data[i*stride:(i+1)*stride]
            xyz = struct.unpack_from('<iii', row)
            if any(not -32768 <= n <= 32767 for n in xyz):
                return {'planar': planar}
            narrow = struct.pack('<hhh', *xyz)
            assert struct.pack('<iii', *struct.unpack('<hhh', narrow)) == row[:12]
            out.extend(narrow + row[12:])
    small = raw[:60] + small_channels + small_bounds
    small_planar = raw[:60] + shuffle(small_channels, channels, 17) + shuffle(small_bounds, bounds, 10)
    return {'planar': planar, 'i16': small, 'i16_planar': small_planar}


ORIGINAL = np.dtype([('pos','<f4',(3,)),('rot','<f4',(4,)),('scale','<f4'),('hidden','u1')])
QUANTIZED = np.dtype([('pos','<i4',(3,)),('rot','<i2',(4,)),('scale','<u2'),('hidden','u1')])


def error(original, raw, channels):
    a = np.frombuffer(original, ORIGINAL, channels, 52)
    b = np.frombuffer(raw, QUANTIZED, channels, 60)
    origin = np.array(struct.unpack_from('<fff', raw, 40), dtype=np.float32)
    pos = origin + b['pos'].astype(np.float32)/16
    q = b['rot'].astype(np.float32)/np.float32(32767)
    q /= np.linalg.norm(q, axis=1)[:, None]
    qa, qb = a['rot'].astype(float), q.astype(float)
    qa /= np.linalg.norm(qa, axis=1)[:, None]
    qb /= np.linalg.norm(qb, axis=1)[:, None]
    angles = np.degrees(2*np.arccos(np.clip(np.abs(np.sum(qa*qb, axis=1)), 0, 1)))
    assert np.array_equal(a['hidden'], b['hidden'])
    return {'position': float(np.linalg.norm(pos.astype(float)-a['pos'],axis=1).max()),
            'rotationDegrees': float(angles.max()),
            'scale': float(np.abs(b['scale'].astype(float)/1024-a['scale']).max())}


def geometry(path):
    names, blocks = nif(path)
    shapes, counts, sizes = {}, collections.Counter(), collections.Counter()
    for kind, data in blocks:
        counts[kind] += 1
        sizes[kind] += len(data)
        if kind not in ('BSTriShape','BSDynamicTriShape','BSSubIndexTriShape'):
            continue
        name_index, = struct.unpack_from('<I', data)
        name = names[name_index] if name_index != 0xffffffff else ''
        key = f'{kind}:{name}'
        ordinal = 0
        while (key, ordinal) in shapes:
            ordinal += 1
        size, = struct.unpack_from('<I', data, 112)
        skin, = struct.unpack_from('<I', data, 88)
        partition = b''
        if skin != 0xffffffff:
            partition_id, = struct.unpack_from('<I', blocks[skin][1], 4)
            assert blocks[partition_id][0] == 'NiSkinPartition'
            partition = blocks[partition_id][1]
        dynamic = b''
        if kind == 'BSDynamicTriShape':
            dynamic_n, = struct.unpack_from('<I', data, 120+size)
            dynamic = data[124+size:124+size+dynamic_n]
            assert len(dynamic) == dynamic_n
        shapes[key, ordinal] = {'packed': data[100:116+size], 'partition': partition, 'dynamic': dynamic}
    return shapes, dict(counts), dict(sizes)


def compare_shapes(before, after):
    result = {'added': [str(k) for k in after.keys()-before.keys()],
              'removed': [str(k) for k in before.keys()-after.keys()], 'changed': []}
    for key in sorted(before.keys() & after.keys()):
        a, b = before[key], after[key]
        parts = [p for p in a if a[p] != b[p]]
        if not parts:
            continue
        change = {'shape': str(key), 'parts': parts}
        if 'dynamic' in parts and len(a['dynamic']) == len(b['dynamic']):
            aa = np.frombuffer(a['dynamic'], '<f4').reshape(-1,4)
            bb = np.frombuffer(b['dynamic'], '<f4').reshape(-1,4)
            distances = np.linalg.norm(aa[:,:3].astype(float)-bb[:,:3], axis=1)
            change['verticesChanged'] = int(np.count_nonzero(distances))
            change['maxPositionChange'] = float(distances.max())
        result['changed'].append(change)
    return result


def analyze(path, output, zstd):
    report = inspect(path)
    report['archive'] = path.name
    report['summary'] = json.loads((path/'summary.json').read_text(encoding='utf-8'))
    samples, generations, metrics, timing = [], [], collections.defaultdict(list), collections.defaultdict(list)
    dictionaries, previous_shapes = {}, None
    out = output/path.name
    out.mkdir()
    errors = collections.defaultdict(float)
    for kind, data in records(path/'capture.phdiag'):
        if kind == 1:
            gen, raw_n, compressed_n = struct.unpack_from('<QII', data)
            body = zstd.decompress(data[48:], raw_n)
            assert struct.unpack_from('<III', body) == (0x41504c44,2,raw_n-12)
            nif_path = out/f'model-{gen}.nif'
            nif_path.write_bytes(body[12:])
            shapes, counts, sizes = geometry(nif_path)
            detail = {'generation': gen, 'compressedBytes': compressed_n, 'rawBytes': raw_n,
                      'blockCounts': counts, 'blockBytes': sizes}
            if previous_shapes is not None:
                detail['geometryDiff'] = compare_shapes(previous_shapes, shapes)
            previous_shapes = shapes
            generations.append(detail)
            if len(generations) == 1:
                compression = {}
                for level in (3,6,9):
                    start=time.perf_counter(); encoded=zstd.compress(body,level)
                    ms=(time.perf_counter()-start)*1000
                    assert zstd.decompress(encoded,len(body)) == body
                    if level == 3:
                        assert encoded == data[48:]
                    compression[level]={'bytes':len(encoded),'ms':ms}
                detail['compressionExperiment'] = compression
        if kind != 2:
            continue
        (capture_ms,camera), at = take(data,0,'dB')
        actor, at = take(data,at,'QQQffffff')
        (original_n,raw_n,compressed_n), at = take(data,at,'III')
        original, raw, compressed = data[at:at+original_n], data[at+original_n:at+original_n+raw_n], data[-compressed_n:]
        magic,version,gen,seq,context,timestamp,*tail = struct.unpack_from('<IIQQQQfffII',raw)
        channels,bounds = tail[-2:]
        assert zstd.decompress(compressed,raw_n) == raw
        start=time.perf_counter(); baseline=zstd.compress(raw)
        timing['zstd1'].append((time.perf_counter()-start)*1000)
        assert baseline == compressed
        # Current archives store byte planes. Experiments operate on reversible
        # interleaved records while baseline still measures the archived bytes.
        if struct.unpack_from('<I',raw,4)[0] == 3:
            body=raw[60:60+23*channels]; tail=raw[60+23*channels:]
            raw=raw[:60]+np.frombuffer(body,dtype=np.uint8).reshape(23,channels).T.copy().tobytes()+np.frombuffer(tail,dtype=np.uint8).reshape(16,bounds).T.copy().tobytes()
        for k,v in error(original,raw,channels).items():
            errors[k] = max(errors[k],v)
        row={'generation':gen,'sequence':seq,'timeUs':timestamp,'captureMs':capture_ms,'firstPerson':bool(camera)}
        samples.append(row)
        if seq == 1:
            generations[-1].update({'timeUs':timestamp,'captureMs':capture_ms})
        metrics['current'].append(compressed_n)
        metrics['clientPacket'].append(packet_size(compressed_n,gen,seq,context,timestamp))
        metrics['serverPacket'].append(packet_size(compressed_n,gen,seq,context,timestamp,True))
        for level in (3,5):
            start=time.perf_counter(); candidate=zstd.compress(raw,level)
            timing[f'zstd{level}'].append((time.perf_counter()-start)*1000)
            assert zstd.decompress(candidate,raw_n) == raw
            metrics[f'zstd{level}'].append(len(candidate))
        alternatives=variants(raw,channels,bounds)
        for name, body in alternatives.items():
            start=time.perf_counter(); candidate=zstd.compress(body)
            timing[name].append((time.perf_counter()-start)*1000)
            assert zstd.decompress(candidate,len(body)) == body
            metrics[name].append(len(candidate))
        for name,body in [('base_dictionary',raw),('i16_planar_dictionary',alternatives.get('i16_planar'))]:
            if body is None:
                continue
            key=(gen,name)
            if key not in dictionaries:
                dictionaries[key]=body
                continue  # base must arrive reliably with generation; no self-compression shortcut
            start=time.perf_counter(); candidate=zstd.compress(body,dictionary=dictionaries[key])
            timing[name].append((time.perf_counter()-start)*1000)
            assert zstd.decompress(candidate,len(body),dictionaries[key]) == body
            metrics[name].append(len(candidate))
    report['experiments']={k:distribution(v) for k,v in metrics.items()}
    report['experimentCompressMs']={k:distribution(v) for k,v in timing.items()}
    report['maxErrors']=dict(errors)
    report['generations']=generations
    report['captureSteadyMs']=distribution([s['captureMs'] for s in samples if s['sequence']!=1])
    report['captureModelMs']=distribution([s['captureMs'] for s in samples if s['sequence']==1])
    report['samplesDetail']=samples
    with (path/'capture.phdiag').open('rb') as stream:
        report['archiveSHA256']=hashlib.file_digest(stream,'sha256').hexdigest()
    (out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    return report


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path)
    p.add_argument('--prefix',required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--zstd-library',type=Path,required=True)
    a=p.parse_args()
    if a.output.resolve().is_relative_to(a.root.resolve()):
        p.error('output must be outside source tree')
    a.output.mkdir(parents=True,exist_ok=False)
    z=Zstd(a.zstd_library)
    reports=[]
    for path in sorted(a.root.glob(a.prefix+'*')):
        if not path.is_dir():
            continue
        r=analyze(path,a.output,z); reports.append(r)
        print(path.name,json.dumps({'sizes':{k:round(v['mean'],2) for k,v in r['experiments'].items()},
                                    'errors':r['maxErrors'],'generations':len(r['generations'])}),flush=True)
    (a.output/'report.json').write_text(json.dumps({'zstdVersion':z.lib.version(),'reports':reports},indent=2),encoding='utf-8')


if __name__ == '__main__':
    main()
