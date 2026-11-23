import ctypes as c, os
from ctypes import wintypes as w
editor=r'E:\unity\2022\2022.3.62f3\Editor'
dll=c.WinDLL('dbghelp',use_last_error=True)
p=42
dll.SymSetOptions(0x2|0x4|0x10)
dll.SymInitializeW.argtypes=[w.HANDLE,w.LPCWSTR,w.BOOL]
assert dll.SymInitializeW(p,editor,False),c.get_last_error()
dll.SymLoadModuleExW.argtypes=[w.HANDLE,w.HANDLE,w.LPCWSTR,w.LPCWSTR,c.c_uint64,w.DWORD,c.c_void_p,w.DWORD]
dll.SymLoadModuleExW.restype=c.c_uint64
base=dll.SymLoadModuleExW(p,None,editor+r'\Unity.exe',None,0x140000000,0,None,0)
assert base,c.get_last_error()
class S(c.Structure):
    _fields_=[('SizeOfStruct',w.ULONG),('TypeIndex',w.ULONG),('Reserved',c.c_uint64*2),('Index',w.ULONG),('Size',w.ULONG),('ModBase',c.c_uint64),('Flags',w.ULONG),('Value',c.c_uint64),('Address',c.c_uint64),('Register',w.ULONG),('Scope',w.ULONG),('Tag',w.ULONG),('NameLen',w.ULONG),('MaxNameLen',w.ULONG),('Name',c.c_char*1)]
cbt=c.WINFUNCTYPE(w.BOOL,c.POINTER(S),w.ULONG,c.c_void_p)
@cbt
def cb(ptr,size,arg):
    s=ptr.contents
    print(hex(s.Address),s.Size,c.string_at(c.addressof(s)+S.Name.offset,s.NameLen).decode())
    return True
dll.SymEnumSymbols.argtypes=[w.HANDLE,c.c_uint64,c.c_char_p,cbt,c.c_void_p]
import sys
for query in sys.argv[1:]:
    dll.SymEnumSymbols(p,base,query.encode(),cb,None)

