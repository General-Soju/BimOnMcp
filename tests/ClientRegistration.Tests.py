"""Exercise the shipped executable against isolated user roots; never edit real settings."""
import argparse, json, os, pathlib, subprocess, tempfile, tomllib

p=argparse.ArgumentParser();p.add_argument('--bridge',type=pathlib.Path,required=True);p.add_argument('--edition',choices=['public','private'],required=True);p.add_argument('--report',type=pathlib.Path,required=True);a=p.parse_args()
bridge=a.bridge.resolve();root=pathlib.Path(tempfile.mkdtemp(prefix='BimOnRegistration-'));results=[]
expected={'BimOn-Revit','BimOn-AutoCAD','BimOn-Navisworks'} | ({'BimOn-Forma'} if a.edition=='private' else set())
def fixture(name):
 base=root/name;env=os.environ.copy()
 for k,v in [('APPDATA','roaming'),('LOCALAPPDATA','local'),('USERPROFILE','user'),('CODEX_HOME','codex'),('CLAUDE_CONFIG_DIR','claude')]:env[k]=str(base/v)
 paths={'legacy':base/'roaming/Claude/claude_desktop_config.json','modern':base/'local/Claude-Data/claude_test/claude_desktop_config.json','code':base/'claude/.claude.json','codex':base/'codex/config.toml'}
 return base,env,paths
def run(env,*args,code=0):
 proc=subprocess.run([str(bridge),'--register-clients',*args],env=env,capture_output=True,timeout=25)
 assert proc.returncode==code,(proc.returncode,proc.stdout,proc.stderr)
 data=json.loads(proc.stdout.decode('utf-8-sig'));assert data['exitCode']==code;return data
def put(path,text):path.parent.mkdir(parents=True,exist_ok=True);path.write_text(text,encoding='utf-8')
def names(path):return set(tomllib.loads(path.read_text(encoding='utf-8-sig')).get('mcp_servers',{})) if path.suffix=='.toml' else set(json.loads(path.read_text(encoding='utf-8-sig')).get('mcpServers',{}))
def passed(name):results.append(name);print('PASS',name)

base,env,paths=fixture('fresh');paths['modern'].parent.mkdir(parents=True)
r=run(env);assert all(names(path)==expected for path in paths.values());assert all(x['status']=='updated' for x in r['results']);passed('first-use JSON/TOML plus existing empty Desktop profile registered')
before={k:v.read_bytes() for k,v in paths.items()};r=run(env);assert all(x['status']=='unchanged' for x in r['results']);assert all(v.read_bytes()==before[k] for k,v in paths.items());assert not list(base.rglob('*.bak'));passed('repeat run is byte-idempotent without redundant backups')
assert not (base/'user/.claude.json').exists() and not (base/'user/.codex').exists();passed('CLAUDE_CONFIG_DIR and CODEX_HOME honored without default-path writes')
base,env,paths=fixture('defaults');env.pop('CODEX_HOME');env.pop('CLAUDE_CONFIG_DIR');run(env);assert names(base/'user/.claude.json')==expected and names(base/'user/.codex/config.toml')==expected;passed('default client locations created before first client launch')
base,env,paths=fixture('selection');run(env,'--clients','codex');assert names(paths['codex'])==expected and not paths['legacy'].exists() and not paths['code'].exists();passed('selected clients only')
base,env,paths=fixture('none');r=run(env,'--clients','none');assert r['results'][0]['status']=='skipped' and not base.exists();passed('all registration tasks deselected creates no settings')
base,env,paths=fixture('diagnostic');r=run(env,'--diagnose',code=10);assert all(x['status']=='needs-registration' for x in r['results']);assert not base.exists();passed('diagnostics do not create configurations')

base,env,paths=fixture('preservation')
j={'preferences':{'keep':True},'mcpServers':{'Other':{'command':'keep'},'BimOn-Revit':{'command':'old','args':[],'disabled':True,'env':{'MY_TEST':'preserve'}}}}
for key in ['legacy','modern','code']:put(paths[key],json.dumps(j))
toml='''# Keep this comment
model = "unchanged"
approval_policy = "never"
notes = """multiline
[mcp_servers.fake]
not a real table"""
[mcp_servers.Other]
command = "keep"
[mcp_servers."BimOn-Revit"]
command = "old"
args = []
enabled = false
tool_timeout_sec = 456
[mcp_servers."BimOn-Revit".tools.execute_script]
approval_mode = "prompt"
'''
put(paths['codex'],toml);run(env)
for key in ['legacy','modern','code']:
 cfg=json.loads(paths[key].read_text(encoding='utf-8'));assert cfg['preferences']['keep'] and cfg['mcpServers']['Other']==j['mcpServers']['Other'];assert cfg['mcpServers']['BimOn-Revit']['disabled'] and cfg['mcpServers']['BimOn-Revit']['env']['MY_TEST']=='preserve'
cfg=tomllib.loads(paths['codex'].read_text(encoding='utf-8'));assert cfg['model']=='unchanged' and cfg['approval_policy']=='never';entry=cfg['mcp_servers']['BimOn-Revit'];assert entry['enabled'] is False and entry['tool_timeout_sec']==456 and entry['tools']['execute_script']['approval_mode']=='prompt';assert 'Keep this comment' in paths['codex'].read_text(encoding='utf-8') and cfg['notes'].endswith('not a real table')
assert all(expected <= names(path) for path in paths.values())
assert all(json.loads(paths[key].read_text(encoding='utf-8'))['mcpServers']['BimOn-Revit']['command']==str(bridge) for key in ['legacy','modern','code'])
assert len(list(base.rglob('*.bak')))==4;passed('existing preferences/servers/comments/multiline/policies/disabled states preserved with backups')
before={k:v.read_bytes() for k,v in paths.items()};run(env);assert all(v.read_bytes()==before[k] for k,v in paths.items());passed('merged complex settings remain byte-idempotent')

for invalid in ['{broken-secret-sentinel','[]','{"mcpServers":[]}','{"mcpServers":{},"mcpServers":{}}']:
 base,env,paths=fixture('invalid-json-'+str(len(results)));put(paths['legacy'],invalid);r=run(env,code=10)
 assert paths['legacy'].read_text(encoding='utf-8')==invalid and names(paths['code'])==expected and names(paths['codex'])==expected;assert 'secret-sentinel' not in json.dumps(r);passed('invalid Desktop JSON preserved; independent clients succeed '+str(len(results)))
for invalid in ['token = "secret-sentinel"\n[broken','mcp_servers = 12','x=1\nx=2']:
 base,env,paths=fixture('invalid-toml-'+str(len(results)));put(paths['codex'],invalid);r=run(env,code=10);assert paths['codex'].read_text(encoding='utf-8')==invalid and names(paths['code'])==expected;assert 'secret-sentinel' not in json.dumps(r);passed('invalid Codex TOML preserved; other clients succeed '+str(len(results)))
base,env,paths=fixture('conflict');original='[mcp_servers.BimOn-Revit]\nurl="https://example.invalid/mcp"\n';put(paths['codex'],original);run(env,code=10);assert paths['codex'].read_text(encoding='utf-8')==original;passed('same-name HTTP server conflict fails closed')
base,env,paths=fixture('late-profile');run(env);paths['modern'].parent.mkdir(parents=True);run(env);assert names(paths['modern'])==expected;passed('repair registers a Desktop profile created after installation')
base,env,paths=fixture('target-selection');run(env,'--targets','revit');assert names(paths['codex'])=={'BimOn-Revit'};passed('target selection respected')
if a.edition=='public':
 base,env,paths=fixture('public-separation');run(env,'--targets','forma',code=10);assert not base.exists();passed('public build cannot register private target')
base,env,paths=fixture('missing-bridge');run(env,'--bridge-path',str(base/'missing.exe'),code=10);assert not base.exists();passed('invalid bridge path cannot create broken registrations')
base,env,paths=fixture('report-collision');put(paths['code'],'{"keep":true}');run(env,'--report',str(paths['code']),code=10);assert paths['code'].read_text(encoding='utf-8')=='{"keep":true}';passed('report cannot overwrite a client config')
a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps({'edition':a.edition,'passed':len(results),'checks':results,'fixtures':str(root)},indent=2),encoding='utf-8')
print('TOTAL',len(results),'passed')
