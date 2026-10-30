# ============================================================
# performance-fields.py —— 开发时生成两张GPU算法数据图，不参与游戏实时战斗。
# 第五章冰胞几何不随时间变化，先离线算好，比每帧每像素重复搜索九邻格更省数学工作。
# 雪、寒潮、原画流动仍由Shader实时计算；这两图不是原画副本，也不能作为Sprite打进图集。
# 使用方法：仓库任意工作目录执行 python tools/performance-fields.py（需要numpy、Pillow）。
# 输出覆盖对应生成图；GameResourceBuild再配置线性/不可读/无损并归入各自章节AB。
# ============================================================
from pathlib import Path
import numpy as np
from PIL import Image
# 目录从本脚本位置推导，不依赖终端当前目录；这是开发素材制作路径，不是游戏资源加载路径。
root = Path(__file__).resolve().parents[1] / 'RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/ChapterBackground'
# 将第五章静态Voronoi冰胞场离线预算：运行时仍按同一p*IceScale坐标查询，不改变雪/寒潮时间。
# n为数据场像素边长（算法精度），不是背景画质或玩法棋盘大小；512²覆盖下面24×24的算法坐标。
n=512
# 使用像素中心：(索引+0.5)/边长。与Shader双线性采样坐标匹配，避免整张场偏移半个像素。
u=(np.arange(n)+.5)/n
x,y=np.meshgrid(u,u)
# 将0～1纹理UV映射到[-12,12]。Shader用(icePosition+12)/24反向查询，两个方向必须同源。
px=x*24-12;py=y*24-12
# 整数格确定最近中心搜索区域，小数位置用于计算像素到相邻中心的偏移。
cellx=np.floor(px);celly=np.floor(py);lx=px-cellx;ly=py-celly
# nearest/second是最近/次近中心的平方距离，初始9大于本3×3搜索内的候选距离。
# sx/sy记录最近中心方向，seed记录该冰胞固定随机种子；都是生成期数组，游戏不保留这些numpy数据。
nearest=np.full_like(x,9);second=nearest.copy();sx=np.zeros_like(x);sy=np.zeros_like(x);seed=np.zeros_like(x)
# 提取小数部分，与Shader的frac对应；输入可包含负数，不能使用截断整数代替floor。
def fract(p):return p-np.floor(p)
# 与ChapterHash对应的确定性哈希：输入格坐标，返回0～1伪随机值；相同坐标总得相同冰胞中心。
# a/b/c为三分量工作数组，33.33/0.1031来自原Shader哈希，修改会改变生成场，需同步并重建AB。
def h(ax,ay):
 a=fract(ax*.1031);b=fract(ay*.1031);c=a.copy();dot=a*(b+33.33)+b*(c+33.33)+c*(a+33.33)
 a+=dot;b+=dot;c+=dot;return fract((a+b)*c)
# 固定3×3相邻格，一次离线计算覆盖整张图。用numpy数组批量运算，避免逐像素Python循环。
for dy in [-1,0,1]:
 for dx in [-1,0,1]:
  # 每个相邻格内放一个带固定扰动的中心；两个hash偏移使X/Y方向不完全相同。
  ix=cellx+dx;iy=celly+dy;hx=h(ix+3.1,iy+3.1);hy=h(ix+17.7,iy+17.7)
  ox=dx+.15+hx*.7-lx;oy=dy+.15+hy*.7-ly;d=ox*ox+oy*oy
  # 若发现更近中心，旧最近降为次近；否则只尝试更新次近。顺序不能反过来而丢掉旧最近距离。
  better=d<nearest;second=np.where(better,nearest,np.minimum(second,d));nearest=np.where(better,d,nearest)
  sx=np.where(better,ox,sx);sy=np.where(better,oy,sy);seed=np.where(better,hx,seed)
# RGBA打包：R=次近距离-最近距离（越小越接近裂隙），G/B=方向×0.5+0.5，A=固定种子。
# 方向原来可为负，编码到0～1后才能存PNG；Shader再用(yz-0.5)×2还原。
field=np.stack([np.maximum(0,np.sqrt(second)-np.sqrt(nearest)),sx*.5+.5,sy*.5+.5,seed],axis=-1)
# PNG行起点与Unity UV y起点相反；不翻转会改变原冰胞斜面方向。
# clip限制PNG可存范围，乘255量化为8位。该量化/双线性近似并非与原算法逐像素完全相同。
data=np.flipud(np.uint8(np.clip(field,0,1)*255))
Image.fromarray(data,'RGBA').save(root/'Chapter05_IceField.png')
# 可平铺周期噪声，用纹理替代全屏hash求值；仅独立GPU采样，不作为Sprite或图集内容。
# 固定1030种子让每次构建可复现；64²基础场放大到256²，导入Repeat让Shader按64坐标周期取样。
# 它是新的近似周期场，会与旧实时噪声有细微外观差别，不能只凭少指令声称目标GPU提高多少。
rng=np.random.default_rng(1030);z=rng.random((64,64)).astype(np.float32)
Image.fromarray(np.uint8(z*255),'L').resize((256,256),Image.Resampling.BICUBIC).save(root/'Chapter04_SwampNoise.png')
print('生成512冰胞预计算场、256周期噪声；不包含原画副本')
