"""使用项目 Unity 自带的 Emscripten 构建 wasm32 决斗库并运行原生回归。"""

import argparse
import os
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory


def build(editor: Path, output: Path) -> None:
    """先验证同一静态库能在 Node 运行，再原子替换 Unity 使用的归档。"""
    core = Path(__file__).resolve().parent
    toolchain = editor.resolve().parent / "Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten"
    python = toolchain / "python/python.exe"
    emscripten = toolchain / "emscripten"
    node = toolchain / "node/node.exe"
    config = toolchain / ".emscripten"
    for tool in (python, emscripten / "em++.py", emscripten / "emar.py", node, config):
        if not tool.is_file():
            raise FileNotFoundError(f"Unity WebGL 工具链缺少 {tool}")

    # 子进程继承 Unity 配套工具配置。中文路径下用 UTF-8 读取 LLVM 输出。
    environment = {**os.environ, "EM_CONFIG": str(config), "PYTHONUTF8": "1"}
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix="revgame-core-webgl-") as work:
        folder = Path(work)
        archive = folder / "libRevGameCore.a"
        compiler = [str(python), str(emscripten / "em++.py"), "-std=c++17", "-O2", "-I", str(core / "Include")]
        objects = []
        # Source 目录只包含生产翻译单元。测试单独链接归档，覆盖同一批 C ABI 符号。
        for source in sorted((core / "Source").glob("*.cpp")):
            obj = folder / (source.stem + ".o")
            subprocess.run([*compiler, "-c", str(source), "-o", str(obj)], check=True, env=environment)
            objects.append(str(obj))
        subprocess.run([str(python), str(emscripten / "emar.py"), "rcs", str(archive), *objects],
                       check=True, env=environment)
        probe = folder / "RevGameCoreChecks.js"
        subprocess.run([*compiler, str(core / "Tests/PipeChecks.cpp"), str(archive),
                        "-s", "ENVIRONMENT=node", "-s", "SINGLE_FILE=1", "-o", str(probe)],
                       check=True, env=environment)
        subprocess.run([str(node), str(probe)], check=True, env=environment)

        # 检查通过后在目标文件系统原子发布，Unity 不会读到半个 .a。
        with TemporaryDirectory(prefix=".revgame-core-stage-", dir=output.parent) as staging:
            ready = Path(staging) / archive.name
            ready.write_bytes(archive.read_bytes())
            ready.replace(output)
    print(f"WebGL 决斗归档已构建并通过测试：{output}")


def main() -> None:
    """接收显式 Unity.exe 和归档路径。构建失败时保留原有归档。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity", type=Path, required=True, help="目标 Unity Editor 的 Unity.exe")
    parser.add_argument("--output", type=Path, required=True, help="生成的 libRevGameCore.a 路径")
    arguments = parser.parse_args()
    build(arguments.unity, arguments.output)


if __name__ == "__main__":
    main()
