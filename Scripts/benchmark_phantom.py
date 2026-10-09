#!/usr/bin/env python3
"""Bounded native-NIF server/transport fixture replay; no engine decoder runs.

Cold/warm names describe server disk cache only. Observers deliberately download
one full shared hash even if they are also publishers. Steady prewarms both ends.
Embedded pose identities stay as recorded: only the opaque server envelope is timed.
This measures server delivery cost, not game decoding or rendering.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import benchmark_enet as bench
import benchmark_enet_workers as workers

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--mode',choices=['off','chat','cold','warm','steady','overload'],required=True)
parser.add_argument('--clients',type=int,required=True)
parser.add_argument('--scenario',choices=['sparse','dense'],default='sparse')
parser.add_argument('--publishers',type=int,default=4)
parser.add_argument('--seconds',type=int,default=15)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--timeout',type=int,default=1800)
parser.add_argument('--workers',type=int,default=8)
parser.add_argument('--rate',type=float,default=10.0)
parser.add_argument('--replication-ms',type=int,default=100)
parser.add_argument('--actor-values-hz',type=float,default=4.0)
parser.add_argument('--maximum',type=int,default=4)
parser.add_argument('--model-file',type=Path)
parser.add_argument('--model-sha256',help='Optional expected SHA256 of the compressed asset')
parser.add_argument('--raw-model-bytes',type=int)
parser.add_argument('--channels',type=int)
parser.add_argument('--pose-file',type=Path,nargs='+',help='Complete compressed production pose payloads, without protobuf envelopes')
parser.add_argument('--static-positions',action='store_true',help='Keep positions fixed while sending movement at the same rate; isolates queues from nearest-view churn')
parser.add_argument('--server-overlay',type=Path)
parser.add_argument('--server-benchmark',type=Path)
args = parser.parse_args()
os.environ['DREAMSLEEVE_BENCH_STATIC_POSITIONS']='1' if args.static_positions else '0'
if not (1 <= args.clients <= 1000 and 1 <= args.seconds <= 300 and 1 <= args.publishers <= args.clients
        and 60 <= args.timeout <= 3600 and 1 <= args.workers <= args.clients and args.clients % args.workers == 0
        and 0 < args.rate <= 20 and 1 <= args.replication_ms <= 1000 and 1 <= args.maximum <= 64 and args.actor_values_hz >= 0):
    parser.error('Invalid bounded clients/workers/duration/rate/recipients')
phantom = args.mode in ('cold','warm','steady','overload')
bench.CLEANUP_SOURCES = 5
if phantom:
    if not args.model_file or not args.pose_file or not args.raw_model_bytes or not args.channels:
        parser.error('Phantom runs require --model-file, --raw-model-bytes, --channels and --pose-file')
    model=args.model_file.read_bytes()
    poses=[path.read_bytes() for path in args.pose_file]
    if not (1 <= len(model) <= 64*2**20 and 1 <= args.raw_model_bytes <= 128*2**20 and 1 <= args.channels <= 4096
            and all(1 <= len(pose) <= 128*2**10 for pose in poses)):
        parser.error('Fixtures exceed native asset/pose format bounds')
    model_hash=hashlib.sha256(model).hexdigest()
    if args.model_sha256 and model_hash != args.model_sha256.lower():
        parser.error('Compressed model SHA256 mismatch')
    probe_config = args.output.resolve().parent/(args.output.name+'-probe.json')
    probe_config.parent.mkdir(parents=True,exist_ok=True)
    probe_config.write_text(json.dumps(dict(Publishers=args.publishers,Rate=args.rate,
        RawModelBytes=args.raw_model_bytes,Channels=args.channels,Maximum=args.maximum,ClientCacheWarm=args.mode=='steady',
        PosePaths=[str(path.resolve()) for path in args.pose_file],ModelPath=str(args.model_file.resolve())),indent=2),encoding='utf-8')
    provenance=dict(modelSha256=model_hash,compressedModelBytes=len(model),rawModelBytes=args.raw_model_bytes,channels=args.channels,
        poses=[dict(path=str(path.resolve()),bytes=len(pose),sha256=hashlib.sha256(pose).hexdigest()) for path,pose in zip(args.pose_file,poses)],
        boundary='Opaque native payload replay; fresh protobuf sequence/time; embedded payload identity unchanged; no native decoder/renderer',
        arguments={key:str(value) if isinstance(value,Path) else [str(p) for p in value] if isinstance(value,list) else value for key,value in vars(args).items()})
    probe_config.with_suffix('.provenance.json').write_text(json.dumps(provenance,indent=2),encoding='utf-8')
    os.environ['DREAMSLEEVE_BENCH_PHANTOM']=str(probe_config)
else:
    os.environ.pop('DREAMSLEEVE_BENCH_PHANTOM',None)
original_config=bench.configuration

def configuration(clients,port,profile,case):
    config=original_config(clients,port,profile,case)
    cache=case/'cache'; cache.mkdir()
    config['Phantoms']['Enabled']=phantom
    config['Phantoms']['StoragePath']=str(cache.resolve())
    config['Phantoms']['Maximum']=args.maximum
    config['Phantoms']['PoseIntervalMs']=max(1,round(1000/args.rate))
    config['Phantoms']['ReplicationIntervalMs']=args.replication_ms
    if args.mode in ('warm','steady','overload'):
        (cache/(model_hash+'.zst')).write_bytes(model)
    (case/'cache-initial.json').write_text(json.dumps(dict(mode=args.mode,diskBytes=sum(p.stat().st_size for p in cache.iterdir()),preseeded=args.mode in ('warm','steady','overload'),modelSha256=model_hash if phantom else None),indent=2),encoding='utf-8')
    return config
bench.configuration=configuration
workers.configuration=configuration
OriginalChild=bench.Child

class CacheMetrics:
    def __init__(self,original,cache): self.original,self.cache=original,cache
    def sample(self):
        sample=self.original.sample()
        totals={'.zst':0,'.tmp':0}; entries=0
        for p in self.cache.iterdir():
            try: size=p.stat().st_size
            except FileNotFoundError: continue
            if p.suffix in totals: totals[p.suffix]+=size
            if p.suffix=='.zst': entries+=1
        sample.update(cacheDiskBytes=totals['.zst'],cacheTemporaryBytes=totals['.tmp'],cacheEntries=entries)
        return sample
    def __getattr__(self,name): return getattr(self.original,name)

class MeasuredChild(OriginalChild):
    def __init__(self,command,log_path,env):
        if len(command)==4 and command[:3]==['dotnet',str(bench.SERVER),'--config']:
            command=['dotnet','exec','--runtimeconfig',str(bench.SERVER.with_suffix('.runtimeconfig.json')),str(bench.CLIENT),'--server-config',command[3],'--metrics-output',str(log_path.parent/'metrics.json')]
        super().__init__(command,log_path,env)
        if '--server-config' in command:
            self.metrics=CacheMetrics(self.metrics,log_path.parent/'cache')
bench.Child=MeasuredChild
workers.Child=MeasuredChild
if args.mode=='chat':
    sys.argv=['benchmark_enet.py','--clients',str(args.clients),'--client-hosts',str(args.clients),'--rates','10','--scenarios','chat','--seconds',str(args.seconds),'--repetitions','1','--profile','minimal','--replication-ms',str(args.replication_ms),'--server-buffer','4194304','--client-buffer','1048576','--timeout',str(args.timeout),'--output',str(args.output)]
    status=bench.main()
else:
    sys.argv=['benchmark_enet_workers.py','--clients',str(args.clients),'--hosts',str(args.clients),'--workers',str(args.workers),'--scenario',args.scenario,'--rate',str(args.rate),'--seconds',str(args.seconds),'--warm-positions','--actor-values-hz',str(args.actor_values_hz),'--replication-ms',str(args.replication_ms),'--profile','minimal','--server-buffer','4194304','--client-buffer','1048576','--timeout',str(args.timeout),'--output',str(args.output)]
    if args.server_overlay: sys.argv += ['--server-overlay',str(args.server_overlay.resolve())]
    if args.server_benchmark: sys.argv += ['--server-benchmark',str(args.server_benchmark.resolve())]
    status=workers.main()
# Return real inherited runner status; retain partial model completion separately.
raise SystemExit(status)
