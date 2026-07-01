# functools.py — IronPython 2.7 wrapper
from _functools import reduce, partial

def wraps(wrapped):
    def decorator(wrapper):
        wrapper.__name__ = getattr(wrapped, '__name__', '')
        wrapper.__doc__  = getattr(wrapped, '__doc__',  '')
        return wrapper
    return decorator

def lru_cache(maxsize=128):
    def decorator(fn):
        cache = {}
        def wrapper(*args):
            if args not in cache:
                if len(cache) >= maxsize: cache.clear()
                cache[args] = fn(*args)
            return cache[args]
        wrapper.__name__ = fn.__name__
        return wrapper
    return decorator
