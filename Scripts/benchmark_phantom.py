#!/usr/bin/env python3
"""Bounded protocol21 loopback cases; inherited movement/chat runner is unchanged.

Cold/warm names describe server disk cache only. Observers deliberately download
one full shared hash even if they are also publishers; no engine decoder runs.
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
parser.add_argument('--mode',choices=['off','chat','cold','warm','overload'],required=True)
parser.add_argument('--clients',type=int,choices=[32,128],required=True)
parser.add_argument('--scenario',choices=['sparse','dense'],default='dense')
parser.add_argument('--publishers',type=int,default=4)
parser.add_argument('--seconds',type=int,default=15)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--timeout',type=int,default=240)
args = parser.parse_args()
if not 1 <= args.seconds <= 90 or not 1 <= args.publishers <= args.clients or not 60 <= args.timeout <= 360:
    parser.error('Require seconds1..90, publishers1..N, timeout60..360')
phantom = args.mode in ('cold','warm','overload')
bench.CLEANUP_SOURCES = 5
model_path = args.output.resolve().parent/'opaque-13MiB.bin'
if phantom:
    if not model_path.exists():
        model_path.parent.mkdir(parents=True,exist_ok=True)
        pattern=bytes(range(251)); size=13*2**20
        model_path.write_bytes((pattern*((size+250)//251))[:size])
    model=model_path.read_bytes()
    assert len(model)==13*2**20
    model_hash=hashlib.sha256(model).hexdigest()
    probe_config = args.output.resolve().parent/(args.output.name+'-probe.json')
    probe_config.write_text(json.dumps(dict(Publishers=args.publishers,Rate=20.0,PoseBytes=6144,ModelPath=str(model_path.resolve()))),encoding='utf-8')
    os.environ['DREAMSLEEVE_BENCH_PHANTOM']=str(probe_config)
else:
    os.environ.pop('DREAMSLEEVE_BENCH_PHANTOM',None)
original_config=bench.configuration

def configuration(clients,port,profile,case):
    config=original_config(clients,port,profile,case)
    cache=case/'cache'; cache.mkdir()
    config['Phantoms']['Enabled']=phantom
    config['Phantoms']['StoragePath']=str(cache.resolve())
    if args.mode in ('warm','overload'):
        (cache/(model_hash+'.zst')).write_bytes(model)
    (case/'cache-initial.json').write_text(json.dumps(dict(mode=args.mode,diskBytes=sum(p.stat().st_size for p in cache.iterdir()),preseeded=args.mode in ('warm','overload'),modelSha256=model_hash if phantom else None),indent=2),encoding='utf-8')
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
    sys.argv=['benchmark_enet.py','--clients',str(args.clients),'--client-hosts',str(args.clients),'--rates','10','--scenarios','chat','--seconds',str(args.seconds),'--repetitions','1','--profile','minimal','--replication-ms','50','--server-buffer','4194304','--client-buffer','1048576','--timeout',str(args.timeout),'--output',str(args.output)]
    status=bench.main()
else:
    sys.argv=['benchmark_enet_workers.py','--clients',str(args.clients),'--hosts',str(args.clients),'--workers','1' if phantom else '4','--scenario','dense' if phantom else args.scenario,'--rate','20','--seconds',str(args.seconds),'--warm-positions','--actor-values-hz','4','--replication-ms','50','--profile','minimal','--server-buffer','4194304','--client-buffer','1048576','--timeout',str(args.timeout),'--output',str(args.output)]
    status=workers.main()
# Return real inherited runner status; retain partial model completion separately.
raise SystemExit(status)
