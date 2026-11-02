---
name: dynamo-mcp
description: BimOn Dynamo MCP(dynamo_* 도구)로 Revit 안에서 실행 중인 Dynamo 그래프를 읽고·제어하고·노드를 만들고·연결하고·실행할 때 사용. "다이나모/Dynamo", dynamo_status/get_graph/get_node_values/set_input/run_current/add_node/add_code_block/add_python_node/connect/delete_node/build_graph, 노드·와이어·슬라이더·Watch·코드블록·Python 노드·.dyn, "그래프 만들어/수정/읽어/실행/벽 두께 노드" 같은 요청에 적용. 빠르게(배치 build_graph)·정확하게(올바른 생성이름·포트·DesignScript/Python) 그래프를 구성하는 규칙·치트시트·함정을 담는다.
---

# Dynamo MCP 사용 스킬 (BimOn-Revit)

실행 중인 **Dynamo for Revit** 세션을 `dynamo_*` MCP 도구로 제어한다. 목표: **속도**(배치)와 **정확성**(올바른 노드/포트/코드).
근거: DynamoDS 소스 및 설치된 API 조사. 실제 버전별 검증 결과는 배포 검증 보고서를 확인한다.

## 0. 전제 (먼저 확인)
- Dynamo가 **Revit 안에서 실행 + 편집기 열림**, 그래프(HomeWorkspace) 1개 열림.
- BimOn-Revit가 **연결된(active) Revit 인스턴스**(팔레트 [연결]).
- 안 되면 `dynamo_status`가 "Dynamo not running/started"를 알려줌 → 사용자에게 Dynamo 열기 요청.

## 1. 표준 워크플로 (항상 이 순서)
1. `dynamo_status` — Dynamo/버전/그래프 확인
2. `list_instances` / `list_documents`로 instanceId·documentId·workspaceId·graphRevision 확인. `dynamo_get_graph`로 GUID와 이름을 읽는다. 동명 노드는 GUID로 지정한다.
3. 계획: 각 작업에 OOTB / CodeBlock / Python 중 무엇이 최적인지 결정(§3)
4. **`dynamo_build_graph`** — 노드+연결을 **한 번에** 생성(§2)
5. JobId가 반환되면 `get_job`으로 terminal까지 확인한 후 `dynamo_get_node_values`와 그래프 상태를 별도로 검증한다. 완료는 저장 완료를 뜻하지 않는다.
6. 수정: `dynamo_set_input`(입력값) / 재빌드 / `dynamo_delete_node`

## 2. 속도 규칙 — 무조건 배치
- **노드를 2개 이상 만들거나 연결이 있으면 반드시 `dynamo_build_graph` 한 번**으로. 개별 `add_*`/`connect` 반복은 소규모 수정·디버깅에만.
- 편집과 Run은 **Manual 모드만 허용**한다. Automatic/Periodic는 MANUAL_MODE_REQUIRED로 거절한다. 배치로 호출 횟수를 줄이고 run=true일 때 평가 완료를 추적한다. 배치는 원자적 트랜잭션이 아니므로 실패하면 그래프를 읽고 부분 변경을 확인한 뒤 복구한다.
- spec 형식:
```jsonc
{ "nodes":[ {"id":"a","node":"Revit.Elements.Element.ElementType","x":0,"y":0},
            {"id":"s","string":"폭"},
            {"id":"gp","node":"Revit.Elements.Element.GetParameterValueByName@string"},
            {"id":"py","python":"OUT=[...]","engine":"","inputs":1} ],
  "connect":[ {"from":"All Elements of Category","to":"a"},
              {"from":"a","to":"gp","toPort":0},
              {"from":"s","to":"gp","toPort":1} ],
  "run":true }
```
- 노드 종류 키는 **하나만**: `node`(OOTB 생성이름) / `codeblock`(DesignScript) / `python`(코드) / `string`(값).
- `from`/`to`는 **이 spec의 id 또는 기존 노드 GUID/유일한 이름**.

## 3. 노드 타입 선택 (혼합이 정답)
| 작업 | 선택 | 이유 |
|---|---|---|
| 표준 작업에 노드가 있음 | **OOTB**(`node`) | 가독성·버전안정·리뷰 (1순위) |
| 짧은 수식·리스트·글루, 제로터치 몇 개 체이닝 | **CodeBlock** | 1노드로 압축 |
| 루프·조건·딕셔너리·정렬/분류, **Revit API 직접**, 외부 라이브러리 | **Python** | OOTB로는 노드 폭증 |
한 그래프에 셋을 **섞어** 쓰는 게 보통 최적(예: OOTB로 수집 → Python으로 분류 → OOTB Watch로 표시).

## 4. OOTB 생성 이름(creation name) — 정확성 핵심
`node` 값에 넣는 문자열 = Dynamo의 `FunctionDescriptor.MangledName`:
- **프로퍼티**: `Namespace.Class.Property` (예 `Revit.Elements.Element.ElementType`) — **`@` 없음**
- **함수**: `Namespace.Class.Method@arg1,arg2` (예 `Revit.Elements.Element.GetParameterValueByName@string`)
- 인자 타입 토큰: `string·int·double·bool·var`, `var[]..[]`(중첩 리스트), `int[]`, 전체 타입명(`Revit.Elements.Element`, `Autodesk.DesignScript.Geometry.Curve`)
- 입력 UI 노드: `String` · `Number` · `Integer Slider` · `Boolean`
- **생성이름을 모르면 추측 금지**: ① **`dynamo_search_nodes(query)`로 조회**(예 `query="GetParameterValue"`, `"List.Filter"`, `"Wall."`) — 정확한 MangledName 반환 ② 그래도 없으면 **CodeBlock**(메서드 문법, 생성이름 불필요)/**Python** 대체 ③ 추가 후 **`dynamo_get_graph`로 state=Active 검증**(미해결이면 Dead).

## 5. 포트 (0-based 인덱스)
- **OOTB 함수노드**: 입력포트 = 함수 인자 순서(**인스턴스 메서드는 element가 in0**), 출력 0 = 반환값.
  - `GetParameterValueByName`: in0=element, **in1=parameterName**, out0=값
  - `Element.ElementType`: in0=element, out0=type
- **Python**: `IN[0], IN[1]…` / `OUT`. `inputs`로 포트 수.
- **CodeBlock**: 미정의 식별자 → 입력포트(등장 순), 각 문장 결과 → 출력포트.
- **자동 매핑(replication)**: `walls(리스트).ElementType` → 리스트 전체에 매핑됨(루프 불필요).

## 6. CodeBlock(DesignScript) 요령
문장 끝 `;` · 미정의 식별자=입력 · 대입/마지막식=출력 · 메서드 문법 `obj.M(arg)` 가능 · 리스트 `{…}`/인덱스 `x[0]`/범위 `0..10..1` · null 전파.
예: `walls.ElementType.GetParameterValueByName("폭");` (입력포트 `walls` 자동 생성)

## 7. Python 노드 요령 (Dynamo 3.x)
- 입력 `IN[0], IN[1]…`, 출력 `OUT`.
- **Revit API**: `x.InternalElement` 로 언래핑 → `Autodesk.Revit.DB` 요소 → API 프로퍼티. 예 `round(IN[0][0].InternalElement.Width*304.8)`(피트→mm).
- **단위**: Revit 내부=피트. mm = 값×304.8.
- **엔진**: 보통 **생략(version 기본 상속, 3.x=CPython3)**. 필요시 `CPython3`/`IronPython2`/`PythonNet3`.

## 8. Revit 데이터 함정
- **벽 두께** = WallType **"폭"(Width) 타입 파라미터**. 인스턴스에 없음 → `Element.ElementType` 먼저 거쳐야 함.
- `GetParameterValueByName` → **프로젝트 표시단위(mm)** 반환.
- **현지화**: 카테고리·파라미터 이름이 한국어("벽","폭") — 영문 가정 금지. 모르면 BimOn-Revit `get_element_parameters`/API로 실제 이름 확인 후 사용.
- 인스턴스 vs 타입 파라미터 구분(대부분의 "Width/면적/높이" 류는 타입).

## 9. 값 읽기·실행 함정
- `run_current`는 **비동기**. `get_job` terminal 확인 후 값 읽기. 응답 유실 시 동일 idempotencyKey와 인자로 조회하고 새 키로 쓰기를 반복하지 않는다. unknown이면 결과를 먼저 조사한다.
- 값 표시: 컬렉션 = `list[N] first=…`. **중첩 리스트는 `first=null`로 보일 수 있음**(표시 한계지 데이터 오류 아님).
- `null (not evaluated)` → 미평가/에러. 노드 **State** 확인: Active(정상)·Warning(에러)·**Dead**(입력 없음/생성이름 미해결).

## 10. 버전 메모
- 설치 호스트의 실제 Dynamo 버전을 get_environment로 확인한다. 지원 연도만으로 엔진/API를 추측하지 않는다.
- Python 엔진은 호스트 기본값 상속을 우선하고, 필요한 경우 설치된 엔진만 선택한다.
- get_node_values는 캐시값이다. MCP 평가와 그래프 리비전이 일치해도 외부 문서 변경의 최신성은 보장하지 않는다.
- 단위는 노드/API마다 확인한다. Revit 내부 길이는 ft이며 WallType.Width 시험값도 내부 길이와 비교한다. 무조건 mm로 간주하지 않는다.

## 11. 흔한 실패 → 회피
- 생성이름 오타 → **Dead** 노드(get_graph로 검증) · 포트 인덱스 오류 · 현지화 파라미터명 · **동명 노드 연결 모호**(예 "Code Block" 2개면 이름으로 못 가림 → 하나만 두거나 OOTB/Python로) · build_graph 미사용 시 느림.

## 12. 검증된 치트시트 (Dynamo 3.x)
- Element: `Revit.Elements.Element.ElementType`, `…GetParameterValueByName@string`, `…Name`, `…BoundingBox`, `…Geometry`, `…GetCategory`
- List: `DSCore.List.GetItemAtIndex@var[]..[],int`, `DSCore.List.Count@var[]..[]`, `DSCore.List.Chop@var[]..[],int[]`
- String: `DSCore.String.Join@string,string[]`, `DSCore.String.Concat@string[]`
- 입력 노드: `String`, `Integer Slider`, `Number`, `Boolean`
- 전체는 **`dynamo_search_nodes`로 조회**(라이브러리 2500+ 그룹). 모르면 추측 말고 조회.
