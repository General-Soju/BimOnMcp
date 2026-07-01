# os.py — IronPython 2.7 Windows OS module wrapper
import nt as _nt
import ntpath as path

sep     = '\\'
altsep  = '/'
pathsep = ';'
linesep = '\r\n'
curdir  = '.'
pardir  = '..'
extsep  = '.'
devnull = 'nul'
name    = 'nt'

getcwd    = _nt.getcwd
listdir   = _nt.listdir
remove    = _nt.remove
unlink    = _nt.remove
rename    = _nt.rename
stat      = _nt.stat
mkdir     = _nt.mkdir
rmdir     = _nt.rmdir
getpid    = _nt.getpid

try: environ = _nt.environ
except: environ = {}

def getenv(key, default=None): return environ.get(key, default)

def makedirs(name, mode=0o777):
    head, tail = path.split(name)
    if not tail:
        head, tail = path.split(head)
    if head and tail and not path.exists(head):
        makedirs(head, mode)
    try: mkdir(name)
    except OSError:
        if not path.isdir(name): raise

def walk(top, topdown=True, onerror=None):
    try: names = listdir(top)
    except OSError:
        if onerror: onerror(OSError(top))
        return
    dirs, files = [], []
    for n in names:
        (dirs if path.isdir(path.join(top, n)) else files).append(n)
    if topdown: yield top, dirs, files
    for d in dirs:
        for x in walk(path.join(top, d), topdown, onerror): yield x
    if not topdown: yield top, dirs, files
