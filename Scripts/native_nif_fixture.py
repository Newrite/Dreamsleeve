#!/usr/bin/env python3
"""Derive a local native-NIF test fixture from an existing prototype archive.

Never writes the source. Retains native geometry/skin bytes verbatim; removes
excluded scene leaves, controllers, and texture dependencies. This is a fixture
converter, not the game's capture implementation or an alternate renderer.
"""
import argparse
from collections import deque
import json
import math
from pathlib import Path
import struct

from phantom_prototype_fixture import nif

NULL = 0xffffffff


def convert(source, output):
    if output.resolve().is_relative_to(source.resolve()):
        raise ValueError('output must be outside the original archive')
    names, blocks = nif(source / 'appearance.nif')
    metadata = json.loads((source / 'metadata.json').read_text(encoding='utf-8-sig'))
    allowed_geometry = {}
    for node in metadata['nodes']:
        if node['geometry']:
            allowed_geometry.setdefault(node['name'], deque()).append(not node['excluded'])
    original_order=[]
    def walk_old(i):
        original_order.append(i)
        kind,data=blocks[i]
        if kind=='NiNode':
            count,=struct.unpack_from('<I',data,72)
            for child in struct.unpack_from('<'+str(count)+'I',data,76):
                if child!=NULL: walk_old(child)
    walk_old(0)
    if len(original_order)!=len(metadata['nodes']): raise ValueError('archive tree order differs')
    original_index={block:i for i,block in enumerate(original_order)}
    native = []
    refs = []
    excluded = set()
    for i, (kind, payload) in enumerate(blocks):
        data, links = bytearray(payload), []
        if kind in ('NiNode', 'BSTriShape', 'BSDynamicTriShape', 'BSSubIndexTriShape'):
            assert struct.unpack_from('<I', data, 4)[0] == 0
            struct.pack_into('<I', data, 8, NULL)
            struct.pack_into('<I', data, 68, NULL)
            if kind == 'NiNode':
                count, = struct.unpack_from('<I', data, 72)
                links = list(range(76, 76 + count * 4, 4))
                assert struct.unpack_from('<I', data, 76 + count * 4)[0] == 0
            else:
                name = names[struct.unpack_from('<I', data)[0]]
                if not allowed_geometry[name].popleft():
                    excluded.add(i)
                links = [88, 92, 96]
        elif kind == 'BSLightingShaderProperty':
            assert struct.unpack_from('<I', data, 8)[0] == 0
            struct.pack_into('<I', data, 12, NULL)
            links = [40]
        elif kind == 'NiAlphaProperty':
            assert struct.unpack_from('<I', data, 4)[0] == 0
            struct.pack_into('<I', data, 8, NULL)
        elif kind == 'BSShaderTextureSet':
            data = bytearray(struct.pack('<10I', 9, *([0] * 9)))
        elif kind in ('NiSkinInstance', 'BSDismemberSkinInstance'):
            count, = struct.unpack_from('<I', data, 12)
            links = [0, 4, 8] + list(range(16, 16 + count * 4, 4))
        elif kind not in ('NiSkinData', 'NiSkinPartition'):
            excluded.add(i)
        native.append((kind, data))
        refs.append(links)
    # Removing auxiliary leaves cannot drop a required skin reference.
    for i, (kind, data) in enumerate(native):
        if kind != 'NiNode':
            continue
        for offset in refs[i]:
            if struct.unpack_from('<I', data, offset)[0] in excluded:
                struct.pack_into('<I', data, offset, NULL)
    retained, pending = set(), [0]
    while pending:
        i = pending.pop()
        if i == NULL or i in retained:
            continue
        if i in excluded or not 0 <= i < len(native):
            raise ValueError('required native reference is unsupported')
        retained.add(i)
        pending.extend(struct.unpack_from('<I', native[i][1], off)[0] for off in refs[i])
    order = sorted(retained)
    remap = {old: new for new, old in enumerate(order)}
    scene=[]
    def walk_new(i):
        scene.append(i)
        kind,data=native[i]
        if kind=='NiNode':
            for offset in refs[i]:
                child,=struct.unpack_from('<I',data,offset)
                if child!=NULL:walk_new(child)
    walk_new(0)
    selected={0}
    bounds=[]
    for i in scene:
        if native[i][0]!='NiNode':
            selected.add(i);bounds.append(i)
            skin,=struct.unpack_from('<I',native[i][1],88)
            if skin!=NULL:
                data=native[skin][1];root,count=struct.unpack_from('<II',data,8)
                selected.add(root);selected.update(struct.unpack_from('<'+str(count)+'I',data,16))
    selected=[original_index[i] for i in scene if i in selected]
    bounds=[original_index[i] for i in bounds]
    types = list(dict.fromkeys(native[i][0] for i in order))
    payloads = []
    for i in order:
        kind, data = native[i]
        for offset in refs[i]:
            old, = struct.unpack_from('<I', data, offset)
            struct.pack_into('<I', data, offset, NULL if old == NULL else remap[old])
        payloads.append(data)
    text = lambda s: struct.pack('<I', len(s.encode())) + s.encode()
    header = b'Gamebryo File Format, Version 20.2.0.7\n'
    header += struct.pack('<IBIII', 0x14020007, 1, 12, len(order), 100) + b'\0\0\0'
    header += struct.pack('<H', len(types)) + b''.join(text(t) for t in types)
    header += b''.join(struct.pack('<H', types.index(native[i][0])) for i in order)
    header += b''.join(struct.pack('<I', len(b)) for b in payloads)
    header += struct.pack('<II', len(names), max(map(lambda n: len(n.encode()), names)))
    header += b''.join(text(n) for n in names) + struct.pack('<I', 0)
    output.mkdir(parents=True, exist_ok=False)
    (output / 'appearance.nif').write_bytes(header + b''.join(payloads) + struct.pack('<II', 1, 0))
    export_poses(source,output,selected,bounds,metadata)
    report = dict(source=str(source), blocks=len(order), types=types,
                  rawBytes=(output / 'appearance.nif').stat().st_size,
                  removedBlocks=len(blocks)-len(order), externalTextures=0)
    (output / 'fixture.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


def quaternion(m):
    trace=m[0]+m[4]+m[8]
    if trace>0:
        s=math.sqrt(trace+1)*2
        q=((m[7]-m[5])/s,(m[2]-m[6])/s,(m[3]-m[1])/s,s/4)
    else:
        i=max(range(3),key=lambda i:m[i*3+i]);j=(i+1)%3;k=(i+2)%3
        s=math.sqrt(1+m[i*3+i]-m[j*3+j]-m[k*3+k])*2
        q=[0.]*4;q[i]=s/4;q[j]=(m[j*3+i]+m[i*3+j])/s;q[k]=(m[k*3+i]+m[i*3+k])/s;q[3]=(m[k*3+j]-m[j*3+k])/s
    norm=math.sqrt(sum(v*v for v in q))
    return [v/norm for v in q]


def export_poses(source,output,channels,bounds,metadata):
    data=(source/'poses.bin').read_bytes()
    magic,version,nodes,frames,rate=struct.unpack_from('<8s4I',data)
    if magic!=b'DSPPOSE1' or version!=1 or nodes!=len(metadata['nodes']):raise ValueError('prototype pose header')
    out=bytearray(struct.pack('<8s3I',b'NIFPOSE2',len(channels),len(bounds),frames));at=24
    for frame in range(frames):
        time,flags=struct.unpack_from('<dB',data,at);at+=9
        poses=[struct.unpack_from('<17fBI',data,at+i*73) for i in range(nodes)];at+=nodes*73
        out.extend(struct.pack('<Q3f',round(time*1000000)+1000000,*poses[0][9:12]))
        for i in channels:
            v=poses[i];out.extend(struct.pack('<8fB',*v[9:12],*quaternion(v[:9]),v[12],v[17] if metadata['nodes'][i]['geometry'] else 0))
        for i in bounds:out.extend(struct.pack('<4f',*poses[i][13:17]))
    if at!=len(data):raise ValueError('prototype pose size')
    (output/'native-poses.bin').write_bytes(out)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    convert(args.source, args.output)
