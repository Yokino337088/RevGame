#!/usr/bin/env python3
"""根据已审核的关键帧清单制作384px运行时副本，完整原帧不修改。--check只核对原帧和输出哈希。

第一次选帧比较97帧的透明预乘RGB/Alpha，在8个时间节点下最小化线性重建误差；清单保存了最终人工预览后的帧号。
重跑使用该清单，避免每次随机换帧。依赖Pillow，制作参数属于纯美术表现，不影响玩法。
"""
from pathlib import Path
import argparse
import hashlib
import json
import sys
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
CLIENT = ROOT / "RevGame_Client"
PROJECT = CLIENT / "UnityProject/RevGame.Unity"
MANIFEST = CLIENT / "美术/AI生成/新版/BOSS关键帧选取清单.json"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    records = json.loads(MANIFEST.read_text(encoding="utf-8"))
    assert [row["chapter"] for row in records] == list(range(1, 7)), "章节清单不完整"
    for row in records:
        assert len(row["frames"]) == 8 and row["size"] == 384, "每章必须是8张384px关键帧"
        for frame in row["frames"]:
            source = CLIENT / frame["source"]
            output = PROJECT / frame["output"]
            assert sha(source) == frame["sourceSha256"], "原帧已改变：" + str(source)
            if not args.check:
                with Image.open(source) as image:
                    image.convert("RGBA").resize((384, 384), Image.Resampling.LANCZOS).save(output, optimize=True)
            assert sha(output) == frame["outputSha256"], "关键帧制作结果已改变：" + str(output)
            with Image.open(output) as image:
                assert image.size == (384, 384) and image.mode == "RGBA" and image.getchannel("A").getbbox(), "帧尺寸/透明像素无效"
    print(json.dumps({"success": True, "chapters": 6, "frames": 48, "sourceFramesUnchanged": True}))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error).encode("ascii", errors="backslashreplace").decode(), file=sys.stderr)
        sys.exit(1)
