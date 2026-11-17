"""Exercise the embedded query against a minimal read-only Revit API double."""
import json,sys,types,unittest
from pathlib import Path
sys.modules['System']=types.ModuleType('System')
diag=types.ModuleType('System.Diagnostics')
diag.Stopwatch=types.SimpleNamespace(StartNew=lambda:types.SimpleNamespace(ElapsedMilliseconds=0))
sys.modules['System.Diagnostics']=diag
class Id:
 def __init__(self,n): self.Value=n
class Level:
 def __init__(self,n,name): self.Id=Id(n);self.Name=name
class Parameter:
 StorageType='String'
 def __init__(self,v): self.v=v
 def AsString(self): return self.v
 def AsValueString(self): return self.v
class Element:
 ViewSpecific=False
 def get_BoundingBox(self,view):return object()
 def __init__(self,n,building='601동',level=1,category=-10,duplicate=False):
  self.Id=Id(n);self.UniqueId='uid'+str(n);self.Name='Wall';self.Category=types.SimpleNamespace(Id=Id(category),Name='벽',CategoryType='Model');self.LevelId=Id(level);self.ps=[Parameter(building)]*(2 if duplicate else 1)
 def GetParameters(self,name): return self.ps if name=='동' else []
 def get_Parameter(self,bip): return None
class RevitLinkInstance(Element): pass
class BasePoint(Element): pass
class Collector:
 def __init__(self,doc): self.doc=doc;self.rows=doc.rows
 def OfClass(self,cls):self.rows=self.doc.levels;return self
 def WhereElementIsNotElementType(self):return self
 def GetElementCount(self):return len(self.rows)
 def __iter__(self):return iter(self.rows)
 def Dispose(self):pass
SCRIPT=(Path(__file__).resolve().parents[2]/'BimOnMcpBridge'/'RevitQuery.py').read_text(encoding='utf-8')
def query(rows=None,criteria=None,action='query_elements',ids=None):
 rows=rows if rows is not None else [Element(1),Element(2,level=2),Element(3,building='602동'),Element(4,building=''),Element(5,level=0)]
 doc=types.SimpleNamespace(rows=rows,levels=[Level(1,'1층'),Level(2,'2층')],ProjectInformation=types.SimpleNamespace(BuildingName='시험단지'),Title='fixture')
 c=dict(buildingMode='parameter',buildingParameter='동',buildingValue=None,levelId=None,categoryId=None);c.update(criteria or {})
 g=dict(json=json,q={'action':action,'criteria':c,'elementIds':ids},doc=doc,FilteredElementCollector=Collector,Level=Level,BuiltInParameter=types.SimpleNamespace(),StorageType=types.SimpleNamespace(String='String',ElementId='ElementId'),CategoryType=types.SimpleNamespace(Model='Model'),RevitLinkInstance=RevitLinkInstance,BasePoint=BasePoint)
 exec(SCRIPT,g)
 return json.loads(g['result'])
class QueryTests(unittest.TestCase):
 def test_and(self):self.assertEqual([x['elementId'] for x in query(criteria=dict(buildingValue='601동',levelId=1,categoryId=-10))['candidates']],[1])
 def test_exact(self):self.assertEqual(query(criteria=dict(buildingValue='601'))['total'],0)
 def test_missing(self):self.assertEqual([x['elementId'] for x in query(criteria=dict(buildingValue=''))['candidates']],[4])
 def test_unknown_level(self):self.assertEqual([x['elementId'] for x in query(criteria=dict(levelId=0))['candidates']],[5])
 def test_duplicate(self):self.assertEqual(query([Element(1,duplicate=True)],dict(buildingValue='601동'))['total'],0)
 def test_duplicate_count(self):self.assertEqual(query([Element(1,duplicate=True)])['ambiguousBuildingCount'],1)
 def test_project_source(self):self.assertEqual(query(criteria=dict(buildingMode='project',buildingValue='시험단지'))['total'],5)
 def test_no_type_fallback(self):self.assertEqual(query(criteria=dict(buildingParameter='unknown',buildingValue='601동'))['total'],0)
 def test_pagination_explicit(self):
  r=query([Element(i) for i in range(1,61)]);self.assertEqual(r['total'],60);self.assertEqual(len(r['candidates']),50);self.assertTrue(r['hasMore'])
 def test_selected_revalidation(self):self.assertEqual([x['elementId'] for x in query(criteria=dict(levelId=1),ids=[1,2])['candidates']],[1])
 def test_link_excluded(self):self.assertEqual(query([RevitLinkInstance(1)])['total'],0)
 def test_base_point_excluded(self):self.assertEqual(query([BasePoint(1)])["total"],0)
 def test_resource_excluded(self):
  e=Element(1);e.get_BoundingBox=lambda view:None;self.assertEqual(query([e])["total"],0)
 def test_options(self):
  r=query(action='get_query_options');self.assertEqual(r['buildings'],['601동','602동']);self.assertEqual(len(r['levels']),2);self.assertEqual(r['missingBuildingCount'],1)
  self.assertEqual(sum(c['count'] for c in r['categoryCounts']),5);self.assertEqual(r['documentNonTypeCount'],5)
 def test_payload_is_data(self):self.assertEqual(query(criteria=dict(buildingValue="x'\nraise Exception('bad')"))['total'],0)
 def test_limit_rejects_partial(self):
  with self.assertRaisesRegex(Exception,'QUERY_LIMIT'):query([Element(i) for i in range(30001)])
 def test_annotations_do_not_consume_model_instance_budget(self):
  annotation=Element(99999);annotation.Category.CategoryType='Annotation'
  r=query([annotation]*31000+[Element(1)])
  self.assertEqual(r['total'],1);self.assertEqual(r['scanned'],1)
 def test_nonplaced_resources_do_not_consume_model_instance_budget(self):
  resource=Element(99999);resource.get_BoundingBox=lambda view:None
  r=query([resource]*31000+[Element(1)])
  self.assertEqual(r['total'],1);self.assertEqual(r['scanned'],1)
if __name__=='__main__':unittest.main(verbosity=2)
