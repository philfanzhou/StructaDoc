"""Bounded sequential evaluation. Acquire the shared heavy lock before invoking."""
import argparse
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import signal
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
DOCS = ['book', 'printed-toc', 'numbered-report', 'heading-styles', 'unstructured']

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--cli', required=True, type=Path)
    p.add_argument('--work', required=True, type=Path)
    p.add_argument('--backend', choices=['pipeline', 'vlm-engine'], required=True)
    p.add_argument('--timeout', type=int, default=600)
    p.add_argument('--document', choices=DOCS)
    args = p.parse_args()
    args.work = args.work.resolve()
    args.work.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(HF_HOME=str(args.work/'hf'), MODELSCOPE_CACHE=str(args.work/'modelscope'),
               MINERU_TOOLS_CONFIG_JSON=str(args.work/'mineru.json'), MINERU_MODEL_SOURCE='huggingface',
               HF_HUB_DISABLE_IMPLICIT_TOKEN='1', TOKENIZERS_PARALLELISM='false',
               TORCH_HOME=str(args.work/'torch'), XDG_CACHE_HOME=str(args.work/'cache'))
    config = args.work/'mineru.json'
    if not config.exists():
        config.write_text('{"config_version":"1.3.2","models-dir":{}}\n')
    for doc in [args.document] if args.document else DOCS:
        name = f'{args.backend}-{doc}'
        cmd = [str(args.cli.resolve()), '-p', str(ROOT/'corpus'/f'{doc}.pdf'), '-o', str(args.work/'output'/args.backend), '-b', args.backend]
        log_path = args.work/f'{name}.log'
        started = time.monotonic()
        timed_out = False
        with log_path.open('w') as log:
            proc = subprocess.Popen(cmd, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                code = proc.wait(timeout=args.timeout)
            except subprocess.TimeoutExpired:
                timed_out = True
                os.killpg(proc.pid, signal.SIGTERM)
                try:
                    code = proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(proc.pid, signal.SIGKILL)
                    code = proc.wait()
        row = {'document':doc, 'backend':args.backend, 'elapsed_seconds':round(time.monotonic()-started,3),
               'exit_code':code, 'timed_out':timed_out, 'timeout_seconds':args.timeout,
               'command':['mineru','-p',f'corpus/{doc}.pdf','-o',f'<work>/output/{args.backend}','-b',args.backend]}
        (args.work/f'{name}.json').write_text(json.dumps(row, indent=2)+'\n')
        print(json.dumps(row), flush=True)
        if code != 0:
            print(f'Failure log: {log_path}', flush=True)
            # Fail once per backend, leaving the remaining documents explicitly unexecuted.
            break

if __name__ == '__main__':
    main()
