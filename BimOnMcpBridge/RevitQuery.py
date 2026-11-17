# Embedded read-only Revit query. No transaction, writes, files, network or dynamic evaluation.
from System.Diagnostics import Stopwatch
watch = Stopwatch.StartNew()
c = q['criteria']
levels = {}
collector = FilteredElementCollector(doc).OfClass(Level)
try:
    for level in collector:
        levels[int(level.Id.Value)] = level.Name
finally:
    collector.Dispose()

def associated_level(e):
    lid = int(e.LevelId.Value)
    if lid in levels:
        return lid, 'Element.LevelId'
    found = {}
    for key in ['FAMILY_LEVEL_PARAM', 'INSTANCE_REFERENCE_LEVEL_PARAM', 'RBS_START_LEVEL_PARAM', 'SCHEDULE_LEVEL_PARAM', 'WALL_BASE_CONSTRAINT', 'STAIRS_BASE_LEVEL_PARAM']:
        bip = getattr(BuiltInParameter, key, None)
        if bip is None:
            continue
        p = e.get_Parameter(bip)
        if p is not None and p.StorageType == StorageType.ElementId:
            v = int(p.AsElementId().Value)
            if v in levels:
                found[v] = key
    if len(found) == 1:
        lid = list(found.keys())[0]
        return lid, found[lid]
    return 0, 'conflicting' if len(found) > 1 else 'unassigned'

project_building = (doc.ProjectInformation.BuildingName or '').strip()
def building(e):
    if c['buildingMode'] == 'project':
        return project_building, 'resolved' if project_building else 'missing'
    ps = list(e.GetParameters(c['buildingParameter']))
    if len(ps) != 1:
        return '', 'ambiguous' if len(ps) > 1 else 'missing'
    p = ps[0]
    value = (p.AsString() if p.StorageType == StorageType.String else p.AsValueString()) or ''
    value = value.strip()
    return value, 'resolved' if value else 'missing'

rows = []
categories = {}
category_counts = {}
buildings = set()
unknown_level = missing_building = ambiguous_building = scanned = 0
selected = set(q.get('elementIds') or [])
collector = FilteredElementCollector(doc).WhereElementIsNotElementType()
document_non_type_count = collector.GetElementCount()
try:
    for e in collector:
        if watch.ElapsedMilliseconds > 8000:
            raise Exception('QUERY_LIMIT: More than 8 seconds; no partial search accepted.')
        cat = e.Category
        if cat is None or cat.CategoryType != CategoryType.Model or isinstance(e, (RevitLinkInstance, BasePoint)):
            continue
        # Revit also classifies materials/project settings as Model categories.
        # Business candidates here are placed instances with model-space bounds.
        if e.ViewSpecific or e.get_BoundingBox(None) is None:
            continue
        # The instance budget applies to the advertised model scope, not annotations,
        # view resources and settings that are discarded above. The time budget still
        # covers the entire traversal; no partial result is accepted.
        scanned += 1
        if scanned > 30000:
            raise Exception('QUERY_LIMIT: More than 30000 model instances; no partial search accepted.')
        eid = int(e.Id.Value)
        if selected and eid not in selected:
            continue
        cid = int(cat.Id.Value)
        categories[cid] = cat.Name
        category_counts[cid] = category_counts.get(cid, 0) + 1
        lid, level_source = associated_level(e)
        bvalue, bstate = building(e)
        if len(bvalue) > 160:
            raise Exception('QUERY_LIMIT: Building value exceeds 160 characters.')
        if lid == 0:
            unknown_level += 1
        if bstate == 'missing':
            missing_building += 1
        if bstate == 'ambiguous':
            ambiguous_building += 1
        if bstate == 'resolved':
            buildings.add(bvalue)
        if q['action'] == 'get_query_options':
            continue
        if c.get('categoryId') is not None and c['categoryId'] != cid:
            continue
        if c.get('levelId') is not None and c['levelId'] != lid:
            continue
        if c.get('buildingValue') is not None and (bstate == 'ambiguous' or c['buildingValue'].strip() != bvalue):
            continue
        rows.append({'elementId': eid, 'uniqueId': e.UniqueId, 'name': e.Name or '(unnamed)',
                     'categoryId': cid, 'category': cat.Name, 'levelId': lid,
                     'level': levels.get(lid, ''), 'levelSource': level_source,
                     'building': bvalue, 'buildingState': bstate})
finally:
    collector.Dispose()
rows.sort(key=lambda r: r['elementId'])
result_object = {'queryProtocol': 1, 'action': q['action'], 'documentTitle': doc.Title,
                 'criteria': c, 'scope': 'host_model_instances_only', 'scanned': scanned,
                 'scanCountScope': 'eligible_model_instances_before_criteria',
                 'documentNonTypeCount': document_non_type_count,
                 'unknownLevelCount': unknown_level, 'missingBuildingCount': missing_building,
                 'ambiguousBuildingCount': ambiguous_building}
if q['action'] == 'get_query_options':
    result_object.update({'categories': [{'id': k, 'name': categories[k]} for k in sorted(categories)],
                          'categoryCounts': [{'categoryId': k, 'category': categories[k], 'count': category_counts[k]} for k in sorted(categories)],
                          'levels': [{'id': k, 'name': levels[k]} for k in sorted(levels)],
                          'buildings': sorted(buildings), 'projectBuilding': project_building})
else:
    result_object.update({'total': len(rows), 'hasMore': len(rows) > 50, 'candidates': rows[:50]})
result = json.dumps(result_object, ensure_ascii=False)
if len(result) > 55000:
    raise Exception('QUERY_LIMIT: Result too large; no truncated result accepted.')
