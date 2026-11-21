// 从 RevGame 的 Web Player 产物生成独立的 GitHub Pages 静态目录。
// 原包的 Brotli 文件继续供本机启动器使用，网页发布目录提供浏览器可直接读取的文件。
import { cpSync, existsSync, lstatSync, mkdirSync, readdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { brotliDecompressSync, gunzipSync } from "node:zlib";

// RevGame 公共试玩地址。下载到本地的 Pages 文件会把玩家引导到这里。
const pagesUrl = "https://yokino337088.github.io/RevGame/";

// GitHub Git 对单个对象的硬上限，单位为字节。
const maxGitFileBytes = 100 * 1024 * 1024;

// 从 RevGame 网页模板找到 Unity 加载器及各构建文件的相对引用。
const buildReference = /\b(dataUrl|frameworkUrl|codeUrl|memoryUrl|symbolsUrl|loader\.src)(\s*[:=]\s*buildUrl\s*\+\s*")([\w.-]+)"/g;

// 复制已选定的发布资源。用真实文件和目录保证分支里的资源都可以独立读取。
function copyDirectory(source, destination)
{
    cpSync(source, destination, {
        recursive: true,
        filter(path)
        {
            const item = lstatSync(path);
            if (!item.isDirectory() && !item.isFile())
            {
                throw new Error(`发布资源必须是普通文件或目录：${path}`);
            }
            if (item.isFile() && item.size > maxGitFileBytes)
            {
                throw new Error(`发布资源超过 GitHub 单文件上限：${path}`);
            }
            return true;
        }
    });
}

// 读取一个 Unity 构建文件。Brotli 和 Gzip 在准备阶段解包，其余按构建原格式交付。
function readBuildFile(source, name)
{
    const path = join(source, "Build", name);
    if (name === "." || name === ".." || !lstatSync(path).isFile())
    {
        throw new Error(`Unity 构建引用无效：${name}`);
    }
    const bytes = readFileSync(path);
    if (name.endsWith(".br"))
    {
        return brotliDecompressSync(bytes);
    }
    if (name.endsWith(".gz"))
    {
        return gunzipSync(bytes);
    }
    return bytes;
}

// 校验 AB 清单和实物后生成内容版号。相同资源重新出包时沿用同一缓存地址。
function bundleVersion(streaming, manifest)
{
    const files = new Map([["WebGL", manifest.mainSha256]]);
    for (const bundle of manifest.bundles)
    {
        const parts = bundle.name.split("/");
        if (parts.some(part => !/^[\w.-]+$/.test(part) || part === "." || part === "..") || files.has(bundle.name))
        {
            throw new Error(`WebGL 清单包含重复或无效包名：${bundle.name}`);
        }
        files.set(bundle.name, bundle.sha256);
    }
    const version = createHash("sha256");
    for (const name of [...files.keys()].sort())
    {
        const path = join(streaming, ...name.split("/"));
        if (!lstatSync(path).isFile())
        {
            throw new Error(`WebGL AB 不是普通文件：${name}`);
        }
        const digest = createHash("sha256").update(readFileSync(path)).digest("hex");
        if (digest !== files.get(name))
        {
            throw new Error(`WebGL AB 与清单摘要不符：${name}`);
        }
        version.update(name + ":" + digest + "\n");
    }
    return version.digest("hex").slice(0, 16);
}

// 验证源包和空目标，再写入只包含 Player 所需文件的发布目录。失败时源包仍可使用。
function preparePages(source, destination)
{
    const html = readFileSync(join(source, "index.html"), "utf8");
    const streaming = join(source, "StreamingAssets", "WebGL");
    // Unity 在 Windows 写出的清单可能带 UTF-8 BOM。
    const manifestText = readFileSync(join(streaming, "BuildManifest.json"), "utf8").replace(/^\uFEFF/, "");
    const manifest = JSON.parse(manifestText);
    if (manifest.platform !== "WebGL" || !Array.isArray(manifest.bundles) || !manifest.bundles.length)
    {
        throw new Error("WebGL AB 清单或平台主包不完整。");
    }
    const packageVersion = bundleVersion(streaming, manifest);
    const streamingReference = 'const streamingAssetsUrl = "StreamingAssets";';
    if (!html.includes(streamingReference))
    {
        throw new Error("找不到 Unity 发布模板的 StreamingAssets 根路径声明。");
    }

    // 从网页配置解析构建名。改扩展名的同时核对加载器、数据、框架和 Wasm 是否齐全。
    const references = new Set();
    const outputs = new Map();
    const site = html.replace(buildReference, (_match, role, prefix, name) => {
        references.add(role);
        const file = name.replace(/\.(br|gz)$/, "");
        if (file === "." || file === ".." || outputs.has(file))
        {
            throw new Error(`Unity 构建文件名冲突：${file}`);
        }
        const bytes = readBuildFile(source, name);
        if (!bytes.length || bytes.length > maxGitFileBytes)
        {
            throw new Error(`构建文件为空或超过 GitHub 单文件上限：${file}`);
        }
        outputs.set(file, bytes);
        // 文件名沿用标准扩展名。内容变动时浏览器走新 URL，不会把旧 Loader 与新 Wasm 混用。
        const version = createHash("sha256").update(bytes).digest("hex").slice(0, 16);
        return `${role}${prefix}${file}?v=${version}"`;
    });
    for (const role of ["dataUrl", "frameworkUrl", "codeUrl", "loader.src"])
    {
        if (!references.has(role))
        {
            throw new Error(`找不到 Unity 发布模板中的 ${role} 引用。`);
        }
    }

    // 用空目录准备一整版发布，避免旧二进制文件或本机辅助文件混入。
    const target = join(realpathSync(dirname(destination)), basename(destination));
    const relation = relative(source, target);
    if (!relation || (!isAbsolute(relation) && relation !== ".." && !relation.startsWith(`..${sep}`)))
    {
        throw new Error("请选择位于源包目录之外的发布目录。");
    }
    if (!existsSync(target))
    {
        mkdirSync(target);
    }
    if (!lstatSync(target).isDirectory() || readdirSync(target).length)
    {
        throw new Error("请选择空的发布目录。");
    }
    mkdirSync(join(target, "Build"));
    for (const [file, bytes] of outputs)
    {
        writeFileSync(join(target, "Build", file), bytes);
    }
    copyDirectory(streaming, join(target, "StreamingAssets", packageVersion, "WebGL"));
    const template = join(source, "TemplateData");
    if (existsSync(template))
    {
        copyDirectory(template, join(target, "TemplateData"));
    }

    // 浏览器通过相对路径从仓库子路径取包。文件协议入口改为跳转说明。
    const guide = /(<div id="file-guide" hidden>\s*<div class="panel">\s*<h1>[^<]*<\/h1>)[\s\S]*?(\s*<\/div>)/;
    const landing = site.replace(streamingReference, `const streamingAssetsUrl = "StreamingAssets/${packageVersion}";`)
        .replace(guide, `$1\n        <p>请通过 GitHub Pages 打开游戏。 <a href="${pagesUrl}">在线试玩</a></p>$2`);
    writeFileSync(join(target, "index.html"), landing);
    writeFileSync(join(target, ".nojekyll"), "");
    console.log(`已生成 GitHub Pages 静态目录：${target}`);
}

// 命令行只接受源包与发布目录，便于重复验证同一种出包方式。
const [source, destination] = process.argv.slice(2);
if (!source || !destination || process.argv.length !== 4)
{
    console.error("用法：node tools/WebGLPreview/prepare-github-pages.mjs <Web 源包> <空发布目录>");
    process.exitCode = 1;
}
else
{
    try
    {
        preparePages(realpathSync(source), resolve(destination));
    }
    catch (error)
    {
        console.error(error.message);
        process.exitCode = 1;
    }
}
