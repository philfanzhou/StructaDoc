"""Small deterministic experiments, not a production outline generator."""
import json
import re
from pathlib import Path
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[1]

def key(text):
    return ' '.join(text.split()).casefold()

def score(expected, found):
    remaining = list(expected)
    matched = []
    false_positive = []
    for node in found:
        match = next((x for x in remaining if key(x['title']) == key(node['title'])), None)
        if match is None:
            false_positive.append(node)
        else:
            remaining.remove(match)
            matched.append((match, node))
    return {'expected':len(expected), 'found':len(found), 'matched_titles':len(matched),
            'correct_start':sum(a['page'] == b['page'] for a,b in matched),
            'correct_level':sum(a['level'] == b.get('level') for a,b in matched),
            'correct_start_and_level':sum(a['page'] == b['page'] and a['level'] == b.get('level') for a,b in matched),
            'misses':remaining, 'false_positives':false_positive}

def bookmarks(reader):
    nodes = []
    def walk(items, level):
        for item in items:
            if isinstance(item, list):
                walk(item, level + 1)
            else:
                page = reader.get_destination_page_number(item)
                if page is not None:
                    nodes.append({'title':item.title,'page':page+1,'level':level})
    walk(reader.outline, 1)
    return nodes

def unique_label_page(pages, label):
    matches = [i+1 for i,x in enumerate(pages) if x == label]
    return matches[0] if len(matches) == 1 else None

def printed_toc(lines, labels, page_texts):
    nodes = []
    unmatched = []
    for line in lines:
        m = re.fullmatch(r'(\s*)(.+?)\s*\.{3,}\s*(\S+)\s*', line)
        if not m:
            continue
        title, label = m[2], m[3]
        page = unique_label_page(labels, label)
        # A Page.label match alone is not a Block/start match.
        if page is None or key(title) not in {key(x) for x in page_texts[page-1].splitlines()}:
            unmatched.append({'title':title,'label':label})
            continue
        nodes.append({'title':title,'page':page,'level':2 if len(m[1]) >= 2 else 1})
    return nodes, unmatched

def numbering_level(text):
    m = re.fullmatch(r'(\d+(?:\.\d+)*)\s+\S.*', text)
    if m:
        return m[1].count('.') + 1
    if re.fullmatch(r'(?:Chapter|Part)\s+(?:\d+|[IVXLCDM]+)\b.*', text, re.I):
        return 1
    if re.fullmatch(r'第[一二三四五六七八九十百\d]+[章节部篇].*', text):
        return 1
    return None

def normalize_content_list(items):
    """Only the tested heading/page/text projection; actual .NET output is preferred."""
    result=[]
    for i,x in enumerate(items):
        t=x['type'].lower().replace('_','-')
        level=x.get('text_level')
        title=(t=='text' and isinstance(level,int) and level>0) or t in ['title','heading']
        content=next((x[k] for k in ['text','content','table_body','code_body','list_items','body'] if k in x and x[k] is not None),None)
        if isinstance(content,list):
            content='\n'.join(content) if all(isinstance(v,str) for v in content) else json.dumps(content,separators=(',',':'))
        subtype=f'heading-{min(level,99)}' if title and isinstance(level,int) and level>0 else x.get('sub_type')
        page=x.get('page_idx',x.get('page_id'))
        result.append({'sequence':i,'page':page+1 if page is not None else None,'type':'title' if title else t,'subtype':subtype,'content':content})
    return result

def anchor_nodes(nodes, blocks):
    anchored=[]
    unresolved=[]
    for node in nodes:
        matches=[b for b in blocks if b['page']==node['page'] and key(b['content'] or '')==key(node['title'])]
        if len(matches)==1:
            anchored.append({**node,'block_sequence':matches[0]['sequence']})
        else:
            unresolved.append({**node,'reason':'missing' if not matches else 'ambiguous'})
    return anchored,unresolved

def provider_nodes(blocks):
    nodes=[]
    for b in blocks:
        if b['type']!='title':
            continue
        m=re.fullmatch(r'heading-(\d+)',b['subtype'] or '')
        nodes.append({'title':b['content'] or '', 'page':b['page'], 'level':int(m[1]) if m else None,'block_sequence':b['sequence']})
    return nodes

def main():
    expected=json.loads((ROOT/'expected.json').read_text())['documents']
    rows=[]
    for doc, truth in expected.items():
        reader=PdfReader(ROOT/'corpus'/f'{doc}.pdf')
        texts=[p.extract_text() for p in reader.pages]
        # pypdf.page_labels supplies defaults even when metadata is absent; never use those.
        labels=reader.page_labels if '/PageLabels' in reader.trailer['/Root'] else [None]*len(reader.pages)
        toc,unmatched=printed_toc(texts[0].splitlines(), labels, texts)
        numbered=[{'title':line,'page':i+1,'level':level} for i,text in enumerate(texts) for line in text.splitlines() if (level:=numbering_level(line)) is not None]
        candidates={'pdf-bookmarks':bookmarks(reader),'printed-toc':toc,'generic-numbering':numbered}
        for source,nodes in candidates.items():
            rows.append({'document':doc,'source':source,**score(truth,nodes),'nodes':nodes, 'unmatched_entries':unmatched if source=='printed-toc' else []})
    (ROOT/'results'/'deterministic.json').write_text(json.dumps(rows,indent=2)+'\n')
    anchors=[]
    provider=[]
    for backend in ['pipeline','vlm-engine']:
        for doc, truth in expected.items():
            path=ROOT/'results'/backend/f'{doc}-blocks.json'
            if path.exists():
                blocks=json.loads(path.read_text())
                nodes=provider_nodes(blocks)
                for row in [r for r in rows if r['document']==doc]:
                    bound,unresolved=anchor_nodes(row['nodes'],blocks)
                    anchors.append({'document':doc,'backend':backend,'source':row['source'],**score(truth,bound),'nodes':bound,'unresolved':unresolved})
                provider.append({'document':doc,'backend':backend,'status':'measured',**score(truth,nodes),'nodes':nodes})
            else:
                provider.append({'document':doc,'backend':backend,'status':'not-measured','reason':'See execution.json; no successful normalized output is available.'})
    (ROOT/'results'/'source-anchors.json').write_text(json.dumps(anchors,indent=2)+'\n')
    (ROOT/'results'/'provider.json').write_text(json.dumps(provider,indent=2)+'\n')

if __name__=='__main__':
    main()
