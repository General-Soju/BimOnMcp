# Python 2.7 compatible __future__.py for IronPython 2.7
# This file is needed by IronPython 2.7 engine.Execute() for 'from __future__ import X' syntax.
all_feature_names = [
    "nested_scopes", "generators", "division", "absolute_import",
    "with_statement", "print_function", "unicode_literals",
]
class _Feature(object):
    def __init__(self, optionalRelease, mandatoryRelease, compiler_flag):
        self.optional   = optionalRelease
        self.mandatory  = mandatoryRelease
        self.compiler_flag = compiler_flag
    def getOptionalRelease(self): return self.optional
    def getMandatoryRelease(self): return self.mandatory
    def __repr__(self):
        return "_Feature" + repr((self.optional, self.mandatory, self.compiler_flag))

CO_FUTURE_DIVISION       = 0x2000
CO_FUTURE_ABSOLUTE_IMPORT= 0x4000
CO_FUTURE_WITH_STATEMENT = 0x8000
CO_FUTURE_PRINT_FUNCTION = 0x10000
CO_FUTURE_UNICODE_LITERALS = 0x20000
CO_FUTURE_NESTED_SCOPES  = 0x10
CO_FUTURE_GENERATORS     = 0x20000

nested_scopes    = _Feature((2,1,0,"beta", 1), (2,2,0,"alpha",0), CO_FUTURE_NESTED_SCOPES)
generators       = _Feature((2,2,0,"alpha",1), (2,3,0,"final",0), CO_FUTURE_GENERATORS)
division         = _Feature((2,2,0,"alpha",2), (3,0,0,"alpha",0), CO_FUTURE_DIVISION)
absolute_import  = _Feature((2,5,0,"alpha",1), (3,0,0,"alpha",0), CO_FUTURE_ABSOLUTE_IMPORT)
with_statement   = _Feature((2,5,0,"alpha",1), (2,6,0,"alpha",0), CO_FUTURE_WITH_STATEMENT)
print_function   = _Feature((2,6,0,"alpha",2), (3,0,0,"alpha",0), CO_FUTURE_PRINT_FUNCTION)
unicode_literals = _Feature((2,6,0,"alpha",2), (3,0,0,"alpha",0), CO_FUTURE_UNICODE_LITERALS)
