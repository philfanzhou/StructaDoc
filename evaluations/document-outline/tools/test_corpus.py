import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from docx import Document
from pypdf import PdfReader
from score import ROOT, bookmarks, normalize_content_list

class CorpusTests(unittest.TestCase):
    def test_generator_is_byte_reproducible(self):
        with tempfile.TemporaryDirectory(prefix='outline-generator-') as tmp:
            subprocess.run([sys.executable,str(ROOT/'tools'/'generate.py'),'--output',tmp],check=True)
            names=['book.pdf','printed-toc.pdf','numbered-report.pdf','unstructured.pdf','heading-styles.docx']
            for name in names:
                self.assertEqual((Path(tmp)/name).read_bytes(),(ROOT/'corpus'/name).read_bytes(),name)
    def test_word_conversion_preserves_declared_hierarchy(self):
        doc=Document(ROOT/'corpus'/'heading-styles.docx')
        native=[(p.text,int(p.style.name[-1])) for p in doc.paragraphs if p.style.name.startswith('Heading ')]
        converted=bookmarks(PdfReader(ROOT/'corpus'/'heading-styles.pdf'))
        self.assertEqual(native,[(n['title'],n['level']) for n in converted])
        self.assertEqual([n['page'] for n in converted],[1,1,2])
    def test_pdf_sources_match_authored_expectations(self):
        expected=json.loads((ROOT/'expected.json').read_text())['documents']
        for name in ['book','heading-styles']:
            self.assertEqual(bookmarks(PdfReader(ROOT/'corpus'/f'{name}.pdf')),expected[name])
        reader=PdfReader(ROOT/'corpus'/'printed-toc.pdf')
        self.assertFalse(reader.outline)
        self.assertIn('/PageLabels',reader.trailer['/Root'])
        self.assertEqual(reader.page_labels,['1','2','3'])
        self.assertEqual(expected['unstructured'],[])
    def test_source_results_include_all_documents_and_no_false_positives(self):
        rows=json.loads((ROOT/'results'/'deterministic.json').read_text())
        self.assertEqual(len(rows),15)
        for source,count in [('pdf-bookmarks',6),('printed-toc',3),('generic-numbering',3)]:
            relevant=[x for x in rows if x['source']==source]
            self.assertEqual(sum(x['expected'] for x in relevant),12)
            self.assertEqual(sum(x['correct_start_and_level'] for x in relevant),count)
            self.assertEqual(sum(len(x['false_positives']) for x in relevant),0)
    def test_input_hashes_and_complete_execution_records(self):
        hashes=json.loads((ROOT/'results'/'corpus-sha256.json').read_text())
        for name,digest in hashes.items():
            self.assertEqual(hashlib.sha256((ROOT/'corpus'/name).read_bytes()).hexdigest(),digest)
        executions=json.loads((ROOT/'results'/'execution.json').read_text())
        self.assertEqual(len(executions),10)
        self.assertEqual(len({(x['backend'],x['document']) for x in executions}),10)
        self.assertTrue(all(x['exit_code']==0 and not x['timed_out'] for x in executions))
    def test_provider_results_preserve_quality_and_extra_titles(self):
        rows=json.loads((ROOT/'results'/'provider.json').read_text())
        for backend,joint in [('pipeline',4),('vlm-engine',8)]:
            subset=[r for r in rows if r['backend']==backend]
            self.assertEqual(len(subset),5)
            self.assertEqual(sum(r['correct_start'] for r in subset),12)
            self.assertEqual(sum(r['correct_start_and_level'] for r in subset),joint)
            self.assertEqual(sum(len(r['false_positives']) for r in subset),1)
            self.assertEqual(sum(len(r['misses']) for r in subset),0)
        anchors=json.loads((ROOT/'results'/'source-anchors.json').read_text())
        self.assertEqual(sum(len(r['nodes']) for r in anchors),24)
        self.assertEqual(sum(len(r['unresolved']) for r in anchors),0)
    def test_python_projection_equals_actual_production_normalizer(self):
        paths=list((ROOT/'results').glob('*/*-blocks.json'))
        self.assertEqual(len(paths),10,'Both backends must have five actual normalized outputs')
        for path in paths:
            source=path.with_name(path.name.replace('-blocks.json','-content-list.json'))
            self.assertEqual(normalize_content_list(json.loads(source.read_text())),json.loads(path.read_text()),path.name)

if __name__=='__main__': unittest.main()
