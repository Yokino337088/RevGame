from pathlib import Path
import numpy as np
from PIL import Image
root = Path(__file__).resolve().parents[1] / 'RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/ChapterBackground'
# 将第五章静态Voronoi冰胞场离线预算：运行时仍按同一p*IceScale坐标查询，不改变雪/寒潮时间。
n=512
u=(np.arange(n)+.5)/n
x,y=np.meshgrid(u,u)
px=x*24-12;py=y*24-12
cellx=np.floor(px);celly=np.floor(py);lx=px-cellx;ly=py-celly
nearest=np.full_like(x,9);second=nearest.copy();sx=np.zeros_like(x);sy=np.zeros_like(x);seed=np.zeros_like(x)
def fract(p):return p-np.floor(p)
def h(ax,ay):
 a=fract(ax*.1031);b=fract(ay*.1031);c=a.copy();dot=a*(b+33.33)+b*(c+33.33)+c*(a+33.33)
 a+=dot;b+=dot;c+=dot;return fract((a+b)*c)
for dy in [-1,0,1]:
 for dx in [-1,0,1]:
  ix=cellx+dx;iy=celly+dy;hx=h(ix+3.1,iy+3.1);hy=h(ix+17.7,iy+17.7)
  ox=dx+.15+hx*.7-lx;oy=dy+.15+hy*.7-ly;d=ox*ox+oy*oy
  better=d<nearest;second=np.where(better,nearest,np.minimum(second,d));nearest=np.where(better,d,nearest)
  sx=np.where(better,ox,sx);sy=np.where(better,oy,sy);seed=np.where(better,hx,seed)
field=np.stack([np.maximum(0,np.sqrt(second)-np.sqrt(nearest)),sx*.5+.5,sy*.5+.5,seed],axis=-1)
# PNG行起点与Unity UV y起点相反；不翻转会改变原冰胞斜面方向。
data=np.flipud(np.uint8(np.clip(field,0,1)*255))
Image.fromarray(data,'RGBA').save(root/'Chapter05_IceField.png')
# 可平铺周期噪声，用纹理替代全屏hash求值；仅独立GPU采样，不作为Sprite或图集内容。
rng=np.random.default_rng(1030);z=rng.random((64,64)).astype(np.float32)
Image.fromarray(np.uint8(z*255),'L').resize((256,256),Image.Resampling.BICUBIC).save(root/'Chapter04_SwampNoise.png')
print('生成512冰胞预计算场、256周期噪声；不包含原画副本')
