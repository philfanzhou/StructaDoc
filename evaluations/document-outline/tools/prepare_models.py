"""Fetch the exact measured revisions into a task-owned cache before reproduction."""
import argparse
import json
import os
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser()
p.add_argument('--work',required=True,type=Path)
args=p.parse_args()
work=args.work.resolve()
work.mkdir(parents=True,exist_ok=True)
os.environ['HF_HOME']=str(work/'hf')
os.environ['HF_HUB_DISABLE_IMPLICIT_TOKEN']='1'
from huggingface_hub import snapshot_download
models=json.loads((ROOT/'results'/'environment.json').read_text())['models']
config={'config_version':'1.3.2','model-source':'huggingface','models-dir':{}}
for model in models:
    path=snapshot_download(model['repository'],revision=model['revision'],allow_patterns=model['paths'],token=False)
    kind='pipeline' if model['repository'].endswith('PDF-Extract-Kit-1.0') else 'vlm'
    config['models-dir'][kind]=path
(work/'mineru.json').write_text(json.dumps(config,indent=2)+'\n')
