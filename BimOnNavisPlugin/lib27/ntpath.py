# ntpath.py — minimal Windows path module for IronPython 2.7
import nt as _nt

sep      = '\\'
altsep   = '/'
extsep   = '.'
pathsep  = ';'
curdir   = '.'
pardir   = '..'

def normcase(s):    return s.replace('/', '\\').lower()
def isabs(s):
    s = normcase(s)
    return len(s) >= 3 and s[1] == ':' and s[2] in '/\\' or s[:2] in ('\\\\',)
def join(a, *p):
    path = a
    for b in p:
        if isabs(b): path = b
        elif not path or path[-1] in '/\\': path += b
        else: path += '\\' + b
    return path
def split(p):
    i = max(p.rfind('/'), p.rfind('\\')) + 1
    head, tail = p[:i], p[i:]
    if head and head not in ('/', '\\', p[:3]):
        head = head.rstrip('/\\')
    return head, tail
def splitext(p):
    i = p.rfind('.')
    if i <= max(p.rfind('/'), p.rfind('\\')):
        return p, ''
    return p[:i], p[i:]
def basename(p): return split(p)[1]
def dirname(p):  return split(p)[0]
def exists(path):
    try: _nt.stat(path); return True
    except: return False
def isfile(path):
    try: return (_nt.stat(path).st_mode & 0xF000) == 0x8000
    except: return False
def isdir(path):
    try: return (_nt.stat(path).st_mode & 0xF000) == 0x4000
    except: return False
def abspath(path):
    if not isabs(path):
        path = join(_nt.getcwd(), path)
    parts = path.replace('/', '\\').split('\\')
    out = []
    for p in parts:
        if p == '..':
            if out: out.pop()
        elif p and p != '.':
            out.append(p)
    return '\\'.join(out) if not path.startswith('\\\\') else '\\\\' + '\\'.join(out)
def normpath(path): return abspath(path)
def expandvars(path): return path
def expanduser(path): return path
def getsize(path): return _nt.stat(path).st_size
