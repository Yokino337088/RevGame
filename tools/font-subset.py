#!/usr/bin/env python3
"""制作运行时Noto字库；原件在美术目录，保持GUID与字形，构建检查防止新增文案缺字。

运行 python tools/font-subset.py 生成，--check仅检查。依赖fonttools，安装命令见requirements。
收集代码、序列化文案和Excel，解码Unity的Unicode转义；保守保留注释字符，但不要求注释符号存在。
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.pens.recordingPen import DecomposingRecordingPen

ROOT = Path(__file__).resolve().parents[1]
CLIENT = ROOT / "RevGame_Client"
ASSETS = CLIENT / "UnityProject/RevGame.Unity/Assets"
SOURCE = CLIENT / "美术/第三方素材/字体/NotoSansCJKsc-Bold.full.otf"
TARGET = ASSETS / "GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf"
MANIFEST = CLIENT / "美术/第三方素材/字体/字体精简清单.json"
EXTRA = CLIENT / "美术/第三方素材/字体/额外保留字符.txt"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def inputs():
    """包含生成配置数据，排除Generated代码镜像，避免将导出内容重复当输入。"""
    files = []
    for folder in [ASSETS / "GameScripts", ASSETS / "GameRes", ASSETS / "Scenes"]:
        for path in folder.rglob("*"):
            if path.suffix in {".cs", ".txt", ".asset", ".prefab", ".unity"} and "Generated" not in path.parts:
                files.append(path)
    files.extend(path for path in (CLIENT / "配置表").glob("*.xlsx") if not path.name.startswith("~$"))
    files.append(EXTRA)
    return sorted(set(files))


def unescape(text):
    """只处理字符转义，避免unicode_escape破坏本来就有的UTF8中文。"""
    def replace(match):
        try:
            return chr(int(match.group()[2:], 16))
        except ValueError:
            return match.group()
    return re.sub(r"\\(?:u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|x[0-9a-fA-F]{2,4})", replace, text)


def collect():
    all_chars = set(chr(n) for n in range(32, 127))
    required = set(all_chars)
    records = []
    # 词法跳过C#注释，区分真正的字符串和注释中的装饰符号；字符串仍保守收集插值表达式。
    tokens = re.compile(r'//[^\n]*|/\*.*?\*/|(?:\$@|@\$|@)"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', re.S)
    for path in inputs():
        raw = path.read_bytes()
        records.append({"path": path.relative_to(ROOT).as_posix(), "sha256": sha(raw)})
        if path.suffix == ".xlsx":
            with zipfile.ZipFile(path) as archive:
                text = "\n".join("".join(ET.fromstring(archive.read(name)).itertext()) for name in archive.namelist()
                                 if name.startswith("xl/") and name.endswith(".xml"))
            visible = text
        else:
            text = raw.decode("utf-8-sig")
            if path.suffix == ".cs":
                visible = "\n".join(m.group() for m in tokens.finditer(text) if not m.group().startswith(("//", "/*")))
            else:
                visible = text
        all_chars.update(c for c in unescape(text) if c.isprintable())
        required.update(c for c in unescape(visible) if c.isprintable())
    return all_chars, required, records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    original = SOURCE.read_bytes()
    font = TTFont(SOURCE, recalcTimestamp=False)
    cmap = font.getBestCmap()
    chars, required, records = collect()
    missing = sorted(ord(c) for c in required if ord(c) not in cmap)
    if missing:
        raise RuntimeError("原字体也不包含必需字符：" + ", ".join(f"U+{n:04X}" for n in missing))
    keep = sorted(ord(c) for c in chars if ord(c) in cmap)
    input_digest = sha(json.dumps(records, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    if args.check:
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        assert manifest["sourceSha256"] == sha(original), "字体原件改变"
        assert manifest["inputDigest"] == input_digest, "文案/配置改变，请重新生成字库"
        assert manifest["outputSha256"] == sha(TARGET.read_bytes()), "运行时字库被其它工具覆盖"
        assert manifest["codepoints"] == keep, "字符清单不一致"
        current = TTFont(TARGET).getBestCmap()
        assert all(n in current for n in keep), "字库缺少必需码点"
        print(json.dumps({"success": True, "characters": len(keep), "outputBytes": TARGET.stat().st_size}))
        return
    options = subset.Options()
    options.recalc_timestamp = False
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    options.name_legacy = True
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(unicodes=keep)
    subsetter.subset(font)
    temp = TARGET.with_suffix(".otf.tmp")
    try:
        font.save(temp)
        generated = TTFont(temp)
        assert all(n in generated.getBestCmap() for n in keep), "生成字库缺字"
        # 横向advance/sidebearing不能改变，否则对话排版与伤害数字宽度会漂移。
        original_font = TTFont(SOURCE)
        original_glyphs = original_font.getGlyphSet()
        generated_glyphs = generated.getGlyphSet()
        for n in keep:
            assert original_font['hmtx'][cmap[n]] == generated['hmtx'][generated.getBestCmap()[n]], f"字符度量改变 U+{n:04X}"
            original_pen = DecomposingRecordingPen(original_glyphs)
            generated_pen = DecomposingRecordingPen(generated_glyphs)
            original_glyphs[cmap[n]].draw(original_pen)
            generated_glyphs[generated.getBestCmap()[n]].draw(generated_pen)
            assert original_pen.value == generated_pen.value, f"字形轮廓改变 U+{n:04X}"
        data = temp.read_bytes()
        if not TARGET.exists() or TARGET.read_bytes() != data:
            temp.replace(TARGET)
        else:
            temp.unlink()
    finally:
        if temp.exists():
            temp.unlink()
    manifest = {"revision": 1, "sourceSha256": sha(original), "outputSha256": sha(data),
                "inputDigest": input_digest, "originalBytes": len(original), "outputBytes": len(data),
                "characterCount": len(keep), "glyphCount": len(generated.getGlyphOrder()),
                "codepoints": keep, "inputs": records,
                "unsupportedCommentCodepoints": sorted(ord(c) for c in chars if ord(c) not in cmap)}
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"success": True, "characters": len(keep), "glyphs": manifest["glyphCount"],
                      "originalBytes": len(original), "outputBytes": len(data)}))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error).encode('ascii', errors='backslashreplace').decode(), file=sys.stderr)
        sys.exit(1)
