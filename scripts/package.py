#!/usr/bin/env python3
import argparse,json,pathlib,tarfile,uuid,gzip
r=pathlib.Path(__file__).resolve().parents[1];p=r/'Packages/com.novorizon.rtree'
parser=argparse.ArgumentParser();parser.add_argument('--write-metas',action='store_true');parser.add_argument('--output',type=pathlib.Path);a=parser.parse_args()
info=json.loads((p/'package.json').read_text())
assets=[x for x in sorted(p.rglob('*')) if x.suffix!='.meta']
for path in assets:
    meta=pathlib.Path(str(path)+'.meta')
    if a.write_metas and not meta.exists():
        guid=uuid.uuid5(uuid.NAMESPACE_URL,info['name']+'/'+path.relative_to(p).as_posix()).hex
        text='fileFormatVersion: 2\nguid: '+guid+'\n'+('folderAsset: yes\n' if path.is_dir() else '')
        importer='MonoImporter' if path.suffix=='.cs' else 'AssemblyDefinitionImporter' if path.suffix=='.asmdef' else 'PackageManifestImporter' if path.name=='package.json' else 'DefaultImporter'
        text+=importer+':\n  externalObjects: {}\n'
        if importer=='MonoImporter':text+='  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n'
        text+='  userData:\n  assetBundleName:\n  assetBundleVariant:\n';meta.write_text(text)
    assert meta.exists(),path
assert json.loads((p/'Runtime/RTree.asmdef').read_text())['references']==[]
assert len(list((p/'Runtime').rglob('*.cs')))==19
assert not list((p/'Runtime').glob('*.cs'))
assert {x.name for x in (p/'Runtime').iterdir() if x.is_dir()}=={'Common','RTree','DynamicAabbTree','StaticBvh','TriangleBvh','LooseOctree','KdTree'}
if a.output:
    a.output.parent.mkdir(parents=True,exist_ok=True)
    with a.output.open('wb') as target,gzip.GzipFile(fileobj=target,mode='wb',filename='',mtime=0) as z,tarfile.open(fileobj=z,mode='w') as tar:
        for path in sorted(p.rglob('*')):
            if not path.is_file():continue
            entry=tar.gettarinfo(str(path),'package/'+path.relative_to(p).as_posix());entry.uid=entry.gid=entry.mtime=0;entry.uname=entry.gname=''
            with path.open('rb') as stream:tar.addfile(entry,stream)
    print(a.output)
print('PASS UPM metadata and single canonical runtime source')
