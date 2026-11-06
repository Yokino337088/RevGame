"""从已入库小屋原画生成局部自然效果遮罩；不改原画。R草木、G小屋柔光、B烟囱烟雾。"""
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

def main():
    root = Path(__file__).resolve().parents[1]
    folder = root / 'RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/Lobby'
    art = Image.open(folder / 'MagicCottage.png').convert('RGB')
    regions = np.asarray(Image.open(folder / 'MagicCottageMask.png').convert('RGB'), dtype=np.float32) / 255
    pixels = np.asarray(art, dtype=np.float32) / 255
    red, green, blue = pixels[..., 0], pixels[..., 1], pixels[..., 2]
    # 原画的绿草与树叶依色相分离；天空/水塘/发光晶石从已有美术遮罩排除。
    # 草木位移只作用颜色属于植物的像素，不让圆台、道路、小屋主体一起波动。
    strength = np.clip((green - red * .95) * 12, 0, 1) * np.clip((green - blue) * 5, 0, 1)
    strength *= 1 - np.max(regions, axis=2)
    nature = Image.fromarray((strength * 255).astype('uint8')).filter(ImageFilter.GaussianBlur(1.4))
    width, height = art.size
    magic = Image.new('L', art.size)
    draw = ImageDraw.Draw(magic)
    # 小屋位置属于这张原画的美术绑定，归生成源配置，不是游戏玩法参数。
    draw.polygon([(width*.50,height*.51),(width*.58,height*.11),(width*.84,height*.23),(width*.94,height*.46),(width*.92,height*.75),(width*.59,height*.77)], fill=210)
    magic = magic.filter(ImageFilter.GaussianBlur(18))
    smoke = Image.new('L', art.size)
    draw = ImageDraw.Draw(smoke)
    draw.polygon([(width*.788,0),(width*.861,0),(width*.846,height*.075),(width*.836,height*.115),(width*.836,height*.157),(width*.805,height*.16),(width*.812,height*.104)], fill=255)
    smoke = smoke.filter(ImageFilter.GaussianBlur(8))
    Image.merge('RGB', (nature, magic, smoke)).save(folder / 'MagicCottageNatureMask.png', optimize=True)
    print('Generated R vegetation / G cottage glow / B chimney mask:', art.size)

if __name__ == '__main__':
    main()
