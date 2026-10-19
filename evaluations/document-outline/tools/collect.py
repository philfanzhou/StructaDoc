"""Collect small synthetic outputs and measured environment facts; weights stay outside Git."""
import argparse
import importlib.metadata as metadata
import json
from pathlib import Path
import platform
import re
import shutil
import subprocess
from zipfile import ZipFile

ROOT=Path(__file__).resolve().parents[1]
def main():
    p=argparse.ArgumentParser()
    p.add_argument('--work',type=Path,required=True)
    args=p.parse_args()
    rows=[]
    for path in sorted(args.work.glob('*.json')):
        if path.name=='mineru.json': continue
        value=json.loads(path.read_text())
        if 'backend' in value: rows.append(value)
    (ROOT/'results'/'execution.json').write_text(json.dumps(rows,indent=2)+'\n')
    for backend in ['pipeline','vlm-engine']:
        dst=ROOT/'results'/backend
        dst.mkdir(exist_ok=True)
        for doc in json.loads((ROOT/'expected.json').read_text())['documents']:
            successful=any(x['backend']==backend and x['document']==doc and x['exit_code']==0 and not x['timed_out'] for x in rows)
            if not successful:
                continue
            files=list((args.work/'output'/backend/doc).rglob('*_content_list.json'))
            if len(files)==1:
                shutil.copyfile(files[0],dst/f'{doc}-content-list.json')
                markdown=files[0].parent/f'{doc}.md'
                if not markdown.is_file():
                    raise RuntimeError(f'Missing actual MinerU Markdown: {markdown}')
                shutil.copyfile(markdown,dst/f'{doc}.md')
                with ZipFile(args.work/f'{backend}-{doc}.zip','w') as z:
                    z.write(files[0],'content_list.json')
                    z.write(markdown,'full.md')
    models=[]
    for snapshot in sorted((args.work/'hf'/'hub').glob('models--*/snapshots/*')):
        files=sorted(f for f in snapshot.rglob('*') if f.is_file())
        models.append({'repository':snapshot.parents[1].name.replace('models--','').replace('--','/'),
                       'revision':snapshot.name,'files':len(files),'bytes':sum(f.stat().st_size for f in files),
                       'paths':[str(f.relative_to(snapshot)) for f in files]})
    env={'python':platform.python_version(),'platform':platform.platform(),
         'packages':{x:metadata.version(x) for x in ['mineru','torch','transformers','mlx','mlx-vlm','reportlab','pypdf','python-docx']},
         'models':models}
    from mineru.utils.config_reader import get_device
    engines=sorted({m[1] for log in args.work.glob('vlm-engine-*.log') for line in log.read_text(errors='replace').splitlines() if (m:=re.search(r'Using (.+?) as the inference engine for VLM\.',line))})
    env.update(pipeline_device=get_device(),vlm_engines=engines,engine_evidence='Selected engines read from actual MinerU CLI runtime logs.')
    if platform.system()=='Darwin':
        env['cpu']=subprocess.check_output(['sysctl','-n','machdep.cpu.brand_string'],text=True).strip()
        env['memory_bytes']=int(subprocess.check_output(['sysctl','-n','hw.memsize'],text=True))
    (ROOT/'results'/'environment.json').write_text(json.dumps(env,indent=2)+'\n')
if __name__=='__main__': main()
