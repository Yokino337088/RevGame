$ErrorActionPreference = 'Stop'

# 服务根只使用启动器所在的构建目录，浏览器地址固定以复用本机 IndexedDB 存档。
$webRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$port = 17890
$address = "http://127.0.0.1:$port/"
if (!(Test-Path -LiteralPath (Join-Path $webRoot 'index.html') -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $webRoot 'Build') -PathType Container) -or
    !(Test-Path -LiteralPath (Join-Path $webRoot 'StreamingAssets\WebGL') -PathType Container)) {
    throw '网页包缺少 index.html、Build 或 StreamingAssets/WebGL。请把启动器放在完整的 Web 包根目录。'
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 本机游戏发布包的只读静态服务。使用 IPv4 回环，由 PowerShell 持有监听，避免 HTTP.sys 预订和管理员权限。
public static class RevGameWebPreview
{
    // 只读前 16 KiB 请求头，防止慢连接或超大请求占住工作线程。
    private const int MaxHeaderBytes = 16 * 1024;

    // 监听已经开始的回环端口。每个连接只读一次请求，由工作线程响应并关闭连接。
    public static void Run(TcpListener listener, string root, int port)
    {
        bool stopping = false;
        ConsoleCancelEventHandler stop = delegate(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            Volatile.Write(ref stopping, true);
            listener.Stop();
        };
        Console.CancelKeyPress += stop;
        try
        {
            while (!Volatile.Read(ref stopping))
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (Volatile.Read(ref stopping))
                    {
                        break;
                    }
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    if (Volatile.Read(ref stopping))
                    {
                        break;
                    }
                    throw;
                }
                ThreadPool.QueueUserWorkItem(delegate { Reply(client, root, port); });
            }
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }
    }

    // 读到 CRLFCRLF 后返回请求头，忽略静态文件请求不需要的请求体。
    private static string ReadHeader(Stream stream)
    {
        byte[] bytes = new byte[MaxHeaderBytes];
        for (int count = 0; count < bytes.Length; count++)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                return null;
            }
            bytes[count] = (byte)value;
            if (count >= 3 && bytes[count - 3] == 13 && bytes[count - 2] == 10 && bytes[count - 1] == 13 && value == 10)
            {
                return Encoding.ASCII.GetString(bytes, 0, count + 1);
            }
        }
        return null;
    }

    // 只读取固定 Host 下的入口、构建文件及 WebGL 资源。校验解码后的路径和每个目录，拒绝路径穿越及链接。
    private static string PackageFile(string root, string request)
    {
        if (request.Length == 0 || request[0] != '/')
        {
            return null;
        }
        string relative = Uri.UnescapeDataString(request.Split('?')[0].Substring(1));
        if (relative.Length == 0)
        {
            relative = "index.html";
        }
        if (relative != "index.html" && !relative.StartsWith("Build/", StringComparison.Ordinal)
            && !relative.StartsWith("StreamingAssets/WebGL/", StringComparison.Ordinal))
        {
            return null;
        }
        string path = root;
        foreach (string part in relative.Split('/'))
        {
            if (part == "" || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part.TrimEnd(' ', '.') != part)
            {
                return null;
            }
            path = Path.Combine(path, part);
            if ((!File.Exists(path) && !Directory.Exists(path))
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }
        }
        return File.Exists(path) ? path : null;
    }

    // MIME 由实际内容后缀决定，Brotli/Gzip 尾缀只决定 Content-Encoding。
    private static string MimeType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".html": return "text/html; charset=utf-8";
            case ".js": return "application/javascript";
            case ".wasm": return "application/wasm";
            case ".json": return "application/json";
            case ".css": return "text/css; charset=utf-8";
            default: return "application/octet-stream";
        }
    }

    // 关闭连接前写入正文长度与编码。构建产物原样读取，浏览器按响应头自行解压。
    private static void SendHeader(Stream stream, string status, string type, long length, string encoding)
    {
        string header = "HTTP/1.1 " + status + "\r\nConnection: close\r\nCache-Control: no-cache\r\n"
            + "Cross-Origin-Resource-Policy: same-origin\r\nX-Content-Type-Options: nosniff\r\nContent-Type: "
            + type + "\r\nContent-Length: " + length + "\r\n";
        if (encoding != null)
        {
            header += "Content-Encoding: " + encoding + "\r\n";
        }
        byte[] bytes = Encoding.ASCII.GetBytes(header + "\r\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    // 处理静态 GET 和 HEAD，找不到文件返回 404；刷新页面导致客户端断开时只清理该连接。
    private static void Reply(TcpClient client, string root, int port)
    {
        using (client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 15000;
            try
            {
                using (NetworkStream stream = client.GetStream())
                {
                    string header = ReadHeader(stream);
                    string[] lines = header == null ? new string[0] : header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines.Length == 0 ? new string[0] : lines[0].Split(' ');
                    string host = "127.0.0.1:" + port;
                    bool localHost = Array.Exists(lines, line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)
                        && line.Substring(5).Trim().Equals(host, StringComparison.OrdinalIgnoreCase));
                    if (request.Length != 3 || !localHost || (request[0] != "GET" && request[0] != "HEAD"))
                    {
                        SendHeader(stream, "400 Bad Request", "text/plain", 0, null);
                        return;
                    }
                    string file = PackageFile(root, request[1]);
                    if (file == null)
                    {
                        SendHeader(stream, "404 Not Found", "text/plain", 0, null);
                        return;
                    }
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    string encoding = extension == ".br" ? "br" : extension == ".gz" ? "gzip" : null;
                    string mimePath = encoding == null ? file : Path.GetFileNameWithoutExtension(file);
                    using (FileStream content = File.OpenRead(file))
                    {
                        SendHeader(stream, "200 OK", MimeType(mimePath), content.Length, encoding);
                        if (request[0] == "GET")
                        {
                            content.CopyTo(stream);
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
'@

# 在确认监听成功后再打开浏览器；固定地址占用时保留原实例，并提示继续使用现有页面。
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
$listener.ExclusiveAddressUse = $true
try {
    $listener.Start()
} catch [Net.Sockets.SocketException] {
    throw "无法监听 $address 。若已运行过启动器，请直接打开该地址，或关闭原启动窗口后再试。"
}

try {
    Write-Host "游戏地址 $address"
    Write-Host '试玩时保留此窗口，关闭窗口或按 Ctrl+C 可停止本机服务。'

    # 专用浏览器资料夹稳定保留存档及浏览器状态，启动参数仅作用于这个游戏窗口。
    $profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevGame\WebPreview'
    $browser = $null
    $browserName = $null
    foreach ($candidate in @(
        @{ Name = 'Edge'; Executable = 'Microsoft\Edge\Application\msedge.exe' },
        @{ Name = 'Chrome'; Executable = 'Google\Chrome\Application\chrome.exe' }
    )) {
        foreach ($installRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
            if ($installRoot) {
                $executable = Join-Path $installRoot $candidate.Executable
                if (Test-Path -LiteralPath $executable -PathType Leaf) {
                    $browser = $executable
                    $browserName = $candidate.Name
                    break
                }
            }
        }
        if ($browser) {
            break
        }
    }
    if ($browser) {
        $profile = Join-Path $profileRoot $browserName
        [IO.Directory]::CreateDirectory($profile) | Out-Null
        Start-Process -FilePath $browser -ArgumentList @('--new-window', '--no-first-run', '--no-default-browser-check', ('--user-data-dir="' + $profile + '"'), $address)
    } else {
        Start-Process $address
    }
    [RevGameWebPreview]::Run($listener, $webRoot, $port)
} finally {
    $listener.Stop()
}
