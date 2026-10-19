import unittest
from score import score, numbering_level, unique_label_page, printed_toc, normalize_content_list, provider_nodes, anchor_nodes

class ScoringTests(unittest.TestCase):
    def test_wrong_level_and_page_are_not_correct(self):
        actual=score([{'title':'A','page':1,'level':2}],[{'title':'A','page':2,'level':1}])
        self.assertEqual((actual['correct_start'],actual['correct_level'],actual['correct_start_and_level']),(0,0,0))
    def test_missing_and_false_positive_count_separately(self):
        actual=score([{'title':'A','page':1,'level':1}],[{'title':'B','page':1,'level':1}])
        self.assertEqual((len(actual['misses']),len(actual['false_positives'])),(1,1))
    def test_duplicates_do_not_inflate_recall(self):
        node={'title':'A','page':1,'level':1}
        actual=score([node],[node,node])
        self.assertEqual((actual['matched_titles'],len(actual['false_positives'])),(1,1))
    def test_missing_and_ambiguous_labels_never_match(self):
        self.assertIsNone(unique_label_page([None,'2',None],'1'))
        self.assertIsNone(unique_label_page(['2','2'],'2'))
        self.assertEqual(unique_label_page([None,'2',None],'2'),2)
    def test_toc_requires_title_at_destination(self):
        nodes,missing=printed_toc(['A .... 2'],['1','2'],['Contents','Different'])
        self.assertEqual(nodes,[])
        self.assertEqual(len(missing),1)
    def test_missing_or_duplicate_block_anchor_is_not_guessed(self):
        node={'title':'A','page':1,'level':1}
        block={'content':'A','page':1,'sequence':4}
        self.assertEqual(anchor_nodes([node],[block])[0][0]['block_sequence'],4)
        self.assertEqual(anchor_nodes([node],[])[0],[])
        self.assertEqual(anchor_nodes([node],[block,block])[0],[])
    def test_common_numbering_and_nonheadings(self):
        for title,level in [('1 A',1),('1.2.3 A',3),('Chapter IV A',1),('Part 2 A',1),('第二章 示例',1),('Ordinary prose',None),('42',None)]:
            self.assertEqual(numbering_level(title),level)
    def test_heading_projection_matches_current_canonical_rules(self):
        items=[{'type':'text','text':'A','text_level':2,'page_idx':0}, {'type':'title','text':'B','page_idx':1}, {'type':'text','text':'body','text_level':0,'page_idx':1}]
        nodes=provider_nodes(normalize_content_list(items))
        self.assertEqual(nodes,[{'title':'A','page':1,'level':2,'block_sequence':0},{'title':'B','page':2,'level':None,'block_sequence':1}])

if __name__=='__main__':
    unittest.main()
