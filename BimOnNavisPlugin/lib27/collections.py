# collections.py  — IronPython 2.7 wrapper
from _collections import defaultdict, deque

class OrderedDict(dict):
    def __init__(self, *args, **kw):
        self._keys = []
        dict.__init__(self)
        if args:
            src = args[0]
            pairs = src.items() if hasattr(src, 'keys') else src
            for k, v in pairs:
                self[k] = v
        for k, v in kw.items():
            self[k] = v
    def __setitem__(self, key, value):
        if key not in self:
            self._keys.append(key)
        dict.__setitem__(self, key, value)
    def __delitem__(self, key):
        dict.__delitem__(self, key)
        self._keys.remove(key)
    def __iter__(self):
        return iter(self._keys)
    def keys(self):   return list(self._keys)
    def values(self): return [self[k] for k in self._keys]
    def items(self):  return [(k, self[k]) for k in self._keys]
    def __repr__(self):
        return 'OrderedDict(%r)' % (list(self.items()),)

class Counter(dict):
    def __init__(self, iterable=None, **kw):
        dict.__init__(self)
        if iterable is not None:
            if hasattr(iterable, 'keys'):
                for k, v in iterable.items():
                    self[k] = self.get(k, 0) + v
            else:
                for item in iterable:
                    self[item] = self.get(item, 0) + 1
        for k, v in kw.items():
            self[k] = self.get(k, 0) + v
    def __missing__(self, key): return 0
    def most_common(self, n=None):
        s = sorted(self.items(), key=lambda x: -x[1])
        return s if n is None else s[:n]
    def update(self, iterable=None, **kw):
        if iterable is not None:
            if hasattr(iterable, 'keys'):
                for k in iterable: self[k] = self.get(k, 0) + iterable[k]
            else:
                for item in iterable: self[item] = self.get(item, 0) + 1
        for k, v in kw.items(): self[k] = self.get(k, 0) + v

def namedtuple(typename, field_names):
    if isinstance(field_names, str):
        field_names = field_names.replace(',', ' ').split()
    field_names = tuple(field_names)
    class _NT(tuple):
        _fields = field_names
        __slots__ = ()
        def __new__(cls, *args):
            return tuple.__new__(cls, args)
        def __repr__(self):
            return typename + '(' + ', '.join(
                f + '=' + repr(v) for f, v in zip(field_names, self)) + ')'
        def _asdict(self):
            return OrderedDict(zip(field_names, self))
        def _replace(self, **kw):
            d = self._asdict()
            d.update(kw)
            return _NT(*d.values())
    for i, name in enumerate(field_names):
        setattr(_NT, name, property(lambda self, i=i: self[i]))
    _NT.__name__ = typename
    return _NT
