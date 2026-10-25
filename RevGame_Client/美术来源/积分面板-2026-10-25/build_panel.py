"""从 Ravenmore 原画零件合成积分面板；运行时只用一张 Sprite，不创建装饰节点。

依赖 Pillow。源零件来自随目录保存的 PSD 内嵌 UI Elements 智能对象。
只调色、裁切和组合，不把原素材的示例文字带进游戏。
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageOps
import math

ROOT = Path(__file__).resolve().parent
OUTPUT = ROOT.parents[1] / 'UnityProject/RevGame.Unity/Assets/GameRes/Art/UI/UI_Game_ScorePanel.png'
SIZE = (660, 620)  # 游戏中显示330×310，保留2倍像素密度。


def nine_slice(source, size, border):
    """只拉伸边条和中心，保留手绘转角比例；这在离线完成，游戏端只画一个四边形。"""
    w, h = source.size
    dw, dh = size
    result = Image.new('RGBA', size)
    sx, sy = [0, border, w-border, w], [0, border, h-border, h]
    dx, dy = [0, border, dw-border, dw], [0, border, dh-border, dh]
    for y in range(3):
        for x in range(3):
            part = source.crop((sx[x], sy[y], sx[x+1], sy[y+1]))
            result.paste(part.resize((dx[x+1]-dx[x], dy[y+1]-dy[y]), Image.Resampling.LANCZOS), (dx[x], dy[y]))
    return result


def tint(source, low, high):
    """保留手绘明暗和alpha，只改变配色。"""
    out = ImageOps.colorize(ImageOps.grayscale(source), low, high).convert('RGBA')
    out.putalpha(source.getchannel('A'))
    return out


# 清除PSD画板边缘的散落笔触，只取完整的深色面板主体。
source = Image.open(ROOT/'source-1.png').convert('RGBA').crop((57, 142, 879, 651))
frame = nine_slice(tint(source, '#241d20', '#f1cc85'), (640, 600), 29)
plate = Image.new('RGBA', SIZE)
shadow = Image.new('RGBA', SIZE)
ImageDraw.Draw(shadow).rounded_rectangle((10, 14, 650, 610), radius=18, fill=(0, 8, 20, 190))
plate.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(5)))
plate.alpha_composite(frame, (10, 10))

# 中心保持低亮度青蓝石面；光感靠边缘提亮，不能让滚动数字压在高亮纹理上。
inside = Image.new('RGBA', (602, 556))
pixels = inside.load()
for y in range(inside.height):
    for x in range(inside.width):
        edge = math.exp(-min(x, 601-x)/48)
        light = math.exp(-((x-301)/230)**2 - ((y-40)/180)**2)
        grain = (math.sin(x*1.7+y*.83)+math.sin(x*.19-y*.31))*.65
        pixels[x,y] = (int(10+light*5+grain), int(22+light*15+edge*13+grain), int(37+light*24+edge*16+grain), 255)
plate.alpha_composite(inside, (29, 32))
d = ImageDraw.Draw(plate)
d.rounded_rectangle((30, 31, 630, 589), radius=6, outline=(86, 115, 115, 255), width=2)

# 复用手绘标题条的金属转角和宝石。羊皮纸区域转为深蓝，橙色宝石转为青色。
header = Image.open(ROOT/'source-2.png').convert('RGBA').crop((6, 23, 837, 208))
hp = header.load()
for y in range(header.height):
    for x in range(header.width):
        r,g,b,a = hp[x,y]
        if not a: continue
        lum = .299*r+.587*g+.114*b
        if g > b*1.13 and r > g*1.06 and r > 125:
            if (x < 92 or x > 739) and y < 75 and r > g*1.3:
                hp[x,y] = (int(lum*.28), min(255,int(lum*1.35)), min(255,int(lum*1.6)),a)
            else:
                hp[x,y] = (int(lum*.075),int(lum*.17),int(lum*.26),a)
        else:
            hp[x,y] = (int(30+lum*.88),int(24+lum*.69),int(22+lum*.43),a)
header = header.resize((642, 130),Image.Resampling.LANCZOS)
plate.alpha_composite(header,(9,8))

# 数字区暗槽与细金线都烘焙进同一图，避免为每条线增加UGUI节点与批次。
d = ImageDraw.Draw(plate)
for rect in [(40,144,620,229),(40,265,620,337),(40,440,620,518)]:
    d.rounded_rectangle(rect,radius=9,fill=(7,16,29,255),outline=(47,75,89,255),width=2)
for y in [245,366,535]:
    for x in range(62,598):
        t=math.sin((x-62)/536*math.pi)
        d.line((x,y,x,y+1),fill=(int(42+119*t),int(44+77*t),int(46+24*t),255))
    d.polygon([(330,y-6),(336,y),(330,y+6),(324,y)],fill=(224,183,102,255))
# 侧边金属铆钉与底部蓝宝石加强实物感，避开标题和数字。
gem=Image.open(ROOT/'source-9.png').convert('RGBA')
gem=tint(gem,'#062538','#99f3ff').resize((32,34),Image.Resampling.LANCZOS)
for x in [15,613]: plate.alpha_composite(gem,(x,291))
for x in [24,636]:
    for y in [148,557]:
        d.ellipse((x-3,y-3,x+3,y+3),fill=(207,169,104,255))
OUTPUT.parent.mkdir(parents=True,exist_ok=True)
plate.save(OUTPUT)
print(OUTPUT)
