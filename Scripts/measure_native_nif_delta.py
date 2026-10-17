"""Offline copy/XOR experiment over native NIF blocks, not a production wire format.
Never modifies inputs; verifies exact reconstruction before reporting bytes.
"""
import argparse, hashlib, json, struct, time
from pathlib import Path
import numpy as np
from analyze_native_phantom_recordings import Zstd
from phantom_prototype_fixture import Reader

def parts(path):
    data=path.read_bytes()
    if len(data)>128*1024**2: raise ValueError('oversized NIF')
    line=data.index(b'\n')+1
    r=Reader(data[line:]); version,endian,user,count,stream=r.read('IBIII')
    if (version,endian,user,stream)!=(0x14020007,1,12,100) or count>65535: raise ValueError('NIF version/count')
    for _ in range(3): r.text('B')
    types=[r.text() for _ in range(r.read('H'))]
    ids=[r.read('H') for _ in range(count)]
    sizes=[r.read('I') for _ in range(count)]
    strings,_=r.read('II')
    for _ in range(strings): r.text()
    for _ in range(r.read('I')): r.read('I')
    start=line+r.at
    blocks=[(types[i],r.take(n)) for i,n in zip(ids,sizes)]
    return data,data[:start],blocks,data[line+r.at:]

def measure(a,b,z):
    original,_,old,_=parts(a); target,header,new,footer=parts(b)
    exact={hashlib.sha256(v).digest():i for i,(_,v) in enumerate(old)}
    candidates={}
    for i,(kind,v) in enumerate(old): candidates.setdefault((kind,len(v)),[]).append(i)
    recipe=bytearray(b'NIFDPROBE'+hashlib.sha256(original).digest()+hashlib.sha256(target).digest())
    recipe.extend(struct.pack('<III',len(target),len(header),len(new)));recipe.extend(header)
    restored=bytearray(header);copied=xored=0;start=time.perf_counter()
    for i,(kind,value) in enumerate(new):
        same=exact.get(hashlib.sha256(value).digest())
        if same is not None:
            recipe.extend(struct.pack('<Bi',0,same));restored.extend(old[same][1]);copied+=len(value)
        else:
            base=min(candidates.get((kind,len(value)),[]),key=lambda index:abs(index-i),default=None)
            if base is None:
                recipe.extend(struct.pack('<BI',1,len(value)));recipe.extend(value);restored.extend(value)
            else:
                delta=np.bitwise_xor(np.frombuffer(value,dtype=np.uint8),np.frombuffer(old[base][1],dtype=np.uint8)).tobytes()
                recipe.extend(struct.pack('<BiI',2,base,len(delta)));recipe.extend(delta)
                restored.extend(np.bitwise_xor(np.frombuffer(delta,dtype=np.uint8),np.frombuffer(old[base][1],dtype=np.uint8)).tobytes());xored+=len(value)
    recipe.extend(footer);restored.extend(footer)
    assert restored==target
    packed=z.compress(bytes(recipe),3)
    assert z.decompress(packed,len(recipe))==recipe
    return dict(source=a.name,target=b.name,fullZstdBytes=len(z.compress(target,3)),deltaZstdBytes=len(packed),copiedBytes=copied,xoredBytes=xored,encodeMs=(time.perf_counter()-start)*1000,sha256=hashlib.sha256(target).hexdigest())

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('roots',nargs='+',type=Path);p.add_argument('--zstd-library',required=True,type=Path);p.add_argument('--output',required=True,type=Path);a=p.parse_args()
    z=Zstd(a.zstd_library);rows=[]
    for root in a.roots:
        for directory in sorted({f.parent for f in root.rglob('model-*.nif')}):
            files=sorted(directory.glob('model-*.nif'),key=lambda f:int(f.stem.split('-')[-1]))
            for old,new in zip(files,files[1:]):
                row=measure(old,new,z);row['recording']=directory.name;rows.append(row)
    with a.output.open('x',encoding='utf8') as f: json.dump(rows,f,indent=2)
    print(json.dumps(dict(pairs=len(rows),full=sum(r['fullZstdBytes'] for r in rows),delta=sum(r['deltaZstdBytes'] for r in rows))))
if __name__=='__main__':main()
