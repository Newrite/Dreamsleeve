"""Read-only pose sub-sampling and timing research; not a production codec/renderer.

Uses unquantized diagnostic snapshots, verifies archive structure with the existing
inspector, and compares held-out 20 Hz frames with every-other-frame interpolation.
The separate network timing experiment is synthetic, not a server/game benchmark.
"""
import argparse
import hashlib
import json
import math
import random
import struct
from pathlib import Path
from inspect_phantom_diagnostics import distribution, inspect, MAX_RECORD


def normalize(q):
    n = math.sqrt(sum(x*x for x in q))
    if n < 1e-6:
        raise ValueError('invalid quaternion')
    return tuple(x/n for x in q)


def mix_rotation(a, b, t, spherical=False):
    a, b = normalize(a), normalize(b)
    dot = sum(x*y for x,y in zip(a,b))
    if dot < 0:
        b, dot = tuple(-x for x in b), -dot
    if spherical and dot < .9995:
        angle = math.acos(min(1., dot))
        l, r = math.sin((1-t)*angle)/math.sin(angle), math.sin(t*angle)/math.sin(angle)
    else:
        l, r = 1-t, t
    return normalize(tuple(l*x+r*y for x,y in zip(a,b)))


def rotation_error(a, b):
    return math.degrees(2*math.acos(min(1., abs(sum(x*y for x,y in zip(normalize(a),normalize(b)))))))


def distance(a, b):
    return math.sqrt(sum((x-y)**2 for x,y in zip(a,b)))


def lerp(a, b, t):
    return tuple(x+(y-x)*t for x,y in zip(a,b))


def frames(path):
    with path.open('rb') as f:
        f.read(20)  # The existing inspector validates the complete archive first.
        while h := f.read(8):
            kind, size = struct.unpack('<II',h)
            if size > MAX_RECORD:
                raise ValueError('oversized record')
            data = f.read(size)
            if len(data) != size:
                raise ValueError('truncated record')
            if kind != 2:
                continue
            capture, = struct.unpack_from('<d',data)
            original_n, raw_n, compressed_n = struct.unpack_from('<III',data,57)
            original = data[69:69+original_n]
            generation, sequence, context, time, *tail = struct.unpack_from('<QQQQfffII',original)
            count, bounds = tail[-2:]
            if len(original) != 52+count*33+bounds*16:
                raise ValueError('invalid original snapshot')
            channels = [struct.unpack_from('<8fB',original,52+33*i) for i in range(count)]
            yield dict(generation=generation,sequence=sequence,context=context,time=time,
                       origin=tail[:3],channels=channels,captureMs=capture)


def recording(directory):
    path = directory/'capture.phdiag'
    inspect(path)
    with path.open('rb') as f:
        digest = hashlib.file_digest(f,'sha256').hexdigest()
    poses = list(frames(path))
    errors = {k:[] for k in ('rootUnits','channelUnits','rotationNlerpDegrees','rotationSlerpDegrees','frameMaxChannelUnits','pairSpanMs')}
    comparisons = skipped = visibility_mismatches = visibility_compared = 0
    for i in range(1,len(poses)-1,2):
        a, m, b = poses[i-1:i+2]
        if (len({(p['generation'],p['context'],len(p['channels'])) for p in (a,m,b)}) != 1
                or not a['time'] < m['time'] < b['time'] or b['time']-a['time'] > 250000):
            skipped += 1
            continue
        t = (m['time']-a['time'])/(b['time']-a['time'])
        comparisons += 1
        errors['rootUnits'].append(distance(lerp(a['origin'],b['origin'],t),m['origin']))
        errors['pairSpanMs'].append((b['time']-a['time'])/1000)
        local = []
        for ac,mc,bc in zip(a['channels'],m['channels'],b['channels']):
            visibility_compared += 1
            visibility_mismatches += ac[8] != mc[8]  # Production holds left visibility until the right endpoint.
            if ac[8] or mc[8] or bc[8]:
                continue  # Report continuously visible channels only (bones included).
            e = distance(lerp(ac[:3],bc[:3],t),mc[:3])
            local.append(e)
            errors['channelUnits'].append(e)
            errors['rotationNlerpDegrees'].append(rotation_error(mix_rotation(ac[3:7],bc[3:7],t),mc[3:7]))
            errors['rotationSlerpDegrees'].append(rotation_error(mix_rotation(ac[3:7],bc[3:7],t,True),mc[3:7]))
        if local:
            errors['frameMaxChannelUnits'].append(max(local))
    return dict(recording=directory.name,sha256=digest,frames=len(poses),comparisons=comparisons,skipped=skipped,
                visibilityMismatches=visibility_mismatches,visibilityComparisons=visibility_compared,
                errors={k:distribution(v) for k,v in errors.items()},
                captureSteadyMs=distribution([p['captureMs'] for p in poses if p['sequence']!=1]),
                captureModelStageMs=distribution([p['captureMs'] for p in poses if p['sequence']==1]))


def timing(rate, delay_ms, scenario):
    # Reproduces SourceTime anchoring + Playback buffer mode only, in float seconds.
    # Not a replacement for C++ tests or measured ENet arrival traces.
    rng = random.Random(73021)
    delay = delay_ms/1000
    packets = []
    for n in range(rate*120):
        source = n/rate
        latency = .02
        if scenario == 'jitter20_loss2pct_3fragments':
            if n and rng.random() < 1-.98**3:
                continue
            latency = .02 if not n else max(0.,.02+rng.uniform(-.02,.02))
        elif scenario == 'latency_step20_to180ms' and source >= 30:
            latency = .18
        packets.append((source+latency,source,n))
    packets.sort()
    samples, at, modes, resets = [], 0, dict(interpolation=0,extrapolation=0,clamped=0,waiting=0), 0
    for frame in range(120*60):
        now = frame/60
        while at < len(packets) and packets[at][0] <= now+1e-9:
            arrival, source, sequence = packets[at]; at += 1
            if samples and source <= samples[-1][0]:
                continue
            mapped = arrival if not samples else samples[-1][1]+source-samples[-1][0]
            if samples and (mapped-arrival > delay or arrival-mapped > 5):
                samples.clear(); mapped = arrival; resets += 1
            samples.append((source,mapped,arrival))
            samples = samples[-8:]
        if now < 2:
            continue  # Exclude initial buffer fill.
        target = now-delay
        if not samples or target <= samples[0][1]:
            mode = 'waiting'
        elif target <= samples[-1][1]+1e-9:
            mode = 'interpolation'
        else:
            mode = 'extrapolation' if target-samples[-1][1] <= .1+1e-9 else 'clamped'
        modes[mode] += 1
    total = sum(modes.values())
    return dict(rate=rate,delayMs=delay_ms,scenario=scenario,resets=resets,
                renderFramePercent={k:100*v/total for k,v in modes.items()})


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('recordings',nargs='+',type=Path)
    p.add_argument('--output',required=True,type=Path)
    args = p.parse_args()
    if any(args.output.resolve().is_relative_to(r.resolve()) for r in args.recordings):
        p.error('output must be outside source recordings')
    result = dict(method='Held-out odd frames from original 20 Hz snapshots; no renderer, mesh error or network loss in recording comparison.',
                  recordings=[recording(r) for r in args.recordings],
                  syntheticTiming=[timing(rate,delay,scenario) for scenario in ('clean','jitter20_loss2pct_3fragments','latency_step20_to180ms')
                                   for rate in (10,20) for delay in (100,200,300)])
    with args.output.open('x',encoding='utf-8') as f:
        json.dump(result,f,ensure_ascii=False,indent=2)
    for r in result['recordings']:
        print(r['recording'],r['comparisons'],r['errors']['channelUnits'])


if __name__ == '__main__':
    main()
