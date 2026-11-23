from struct import unpack_from
from ctypes import c_int64
MASK=2**64-1
P1,P2,P3,P4,P5=11400714785074694791,14029467366897019727,1609587929392839161,9650029242287828579,2870177450012600261
def rot(x,n):
    x &= MASK
    return ((x<<n)|(x>>(64-n))) & MASK
def mix(x,y):return rot(x+y*P2,31)*P1 & MASK
def xx(s):
    b=s.encode(); n=len(b); i=0
    if n>=32:
        v=[P1+P2,P2,0,-P1]
        while i+32<=n:
            v=[mix(a,w) for a,w in zip(v,unpack_from('<4Q',b,i))]; i+=32
        h=sum(rot(a,r) for a,r in zip(v,[1,7,12,18])) & MASK
        for a in v:h=((h^mix(0,a))*P1+P4)&MASK
    else:h=P5
    h=(h+n)&MASK
    while i+8<=n:
        h=(rot(h^mix(0,unpack_from('<Q',b,i)[0]),27)*P1+P4)&MASK; i+=8
    if i+4<=n:
        h=(rot(h^unpack_from('<I',b,i)[0]*P1,23)*P2+P3)&MASK; i+=4
    for a in b[i:]:h=rot(h^a*P5,11)*P1 & MASK
    h ^= h>>33; h=h*P2 & MASK; h ^= h>>29; h=h*P3 & MASK; h ^= h>>32
    return c_int64(h).value
if __name__=='__main__':
    for type_name in ['114','MonoBehaviour','InputActionReference','MonoBehaviour:114','InputActionAsset','UnityEngine.InputSystem.InputActionAsset']:
        for name in ['<root>', 'UI/Point','UI/Click']:
            for end in ['', '0','1','-1','00','/0']:
                value=xx(f'Type:{type_name}->{name}{end}')
                if value in [-944628639613478452,1054132383583890850,8056856818456041789]:print(type_name,name,end,value)
