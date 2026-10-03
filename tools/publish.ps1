# 把本项目发布到 GitHub —— 全程只走 REST API，不需要安装 git。
#
# token 从 Windows 凭据管理器读取（就是 git 平时用的那一条），
# 不会写进任何文件，也不会打印出来。
#
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Repo 别的名字 -Tag v1.1.0 -NoRelease
param(
    [string]$Owner       = "woodytakfai",
    [string]$Repo        = "close-all-background",
    [string]$Description = "一键关闭 Windows 后台应用 —— 原生 C# 单文件 EXE，零依赖，37 项自动化测试",
    [string]$Tag         = "v1.0.0",
    [string]$CredTarget  = "git:https://woodytakfai@github.com",
    # Release 附件名。GitHub 的附件接口会把非 ASCII 字符整段清洗掉
    # （中文名会变成 default.exe），所以固定用 ASCII 名。
    [string]$AssetName   = "",
    [switch]$Private,
    [switch]$NoRelease,
    [switch]$AssetOnly
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$api  = "https://api.github.com"
if ([string]::IsNullOrEmpty($AssetName)) { $AssetName = "CloseAllBackground-$Tag.exe" }

# ---------------------------------------------------------------- 取凭据
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class GcmCred {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct CREDENTIAL {
    public int Flags; public int Type;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
    [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
    public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public IntPtr Attributes;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
    [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
  }
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
  [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);
  public static string Read(string target) {
    IntPtr p;
    if (!CredRead(target, 1, 0, out p)) return null;
    try {
      CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
      if (c.CredentialBlobSize <= 0) return "";
      byte[] b = new byte[c.CredentialBlobSize];
      Marshal.Copy(c.CredentialBlob, b, 0, c.CredentialBlobSize);
      return Encoding.Unicode.GetString(b).Trim();
    } finally { CredFree(p); }
  }
}
"@

$token = [GcmCred]::Read($CredTarget)
if (-not $token) { throw "读不到凭据 '$CredTarget'（Windows 凭据管理器里没有）" }
$headers = @{
    Authorization = "token $token"
    "User-Agent"  = "close-all-background-publisher"
    Accept        = "application/vnd.github+json"
}

function Api([string]$method, [string]$path, $body) {
    $uri = if ($path -like "http*") { $path } else { $api + $path }
    if ($null -eq $body) {
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 60
    }
    $json  = $body | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 60 `
        -ContentType "application/json; charset=utf-8" -Body $bytes
}

# ---------------------------------------------------------------- 确认身份
$me = Api GET "/user" $null
Write-Host ("已登录：{0}" -f $me.login) -ForegroundColor Cyan

$exeAbs = Join-Path $root "dist\一键关闭后台应用.exe"
if (-not (Test-Path $exeAbs)) { throw "找不到编译产物：$exeAbs（请先运行 build.ps1）" }

# ---------------------------------------------------------------- 只补传 Release 附件
# 上传失败时（比如 EXE 被运行中的实例或杀毒软件占用）用它重试，不会产生多余提交。
if ($AssetOnly) {
    $rel0 = Api GET "/repos/$Owner/$Repo/releases/tags/$Tag" $null
    foreach ($old in $rel0.assets) {
        $null = Api DELETE "/repos/$Owner/$Repo/releases/assets/$($old.id)" $null
        Write-Host ("  移除旧附件 " + $old.name) -ForegroundColor DarkGray
    }
    $url0  = "https://uploads.github.com/repos/$Owner/$Repo/releases/$($rel0.id)/assets?name=" +
             [uri]::EscapeDataString($AssetName)
    $asset0 = Invoke-RestMethod -Method POST -Uri $url0 -Headers $headers `
        -ContentType "application/octet-stream" -InFile $exeAbs -TimeoutSec 300
    Write-Host ("已挂上附件：{0}" -f $asset0.browser_download_url) -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- 建仓库
$exists = $true
try { $null = Api GET "/repos/$Owner/$Repo" $null } catch { $exists = $false }

if ($exists) {
    Write-Host ("仓库 {0}/{1} 已存在，将追加一个新提交。" -f $Owner, $Repo) -ForegroundColor Yellow
} else {
    $null = Api POST "/user/repos" @{
        name        = $Repo
        description = $Description
        private     = [bool]$Private
        has_issues  = $true
        has_wiki    = $false
        auto_init   = $false
    }
    Write-Host ("已创建 {0}/{1}" -f $Owner, $Repo) -ForegroundColor Green
}

# ---------------------------------------------------------------- 确保仓库非空
# GitHub 的 Git Data API 在“一个提交都没有”的仓库上会返回 409 Git Repository is empty，
# 所以先用 Contents API 放一个占位文件把仓库初始化，最后再用一个孤儿提交整体覆盖它，
# 这样最终历史里只留一个干净的提交。
$priorSha = $null
try {
    $ref = Api GET "/repos/$Owner/$Repo/git/ref/heads/main" $null
    $priorSha = $ref.object.sha
} catch { }

$firstPublish = [string]::IsNullOrEmpty($priorSha)
if ($firstPublish) {
    $placeholder = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("# $Repo`n"))
    $null = Api PUT "/repos/$Owner/$Repo/contents/.init" @{ message = "init"; content = $placeholder }
    Write-Host "仓库原本是空的，已先初始化（最终会被一个干净的提交整体覆盖）。" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 收集文件
$skipDirs  = @("\.git", "\test\fakeprot")
$skipExact = @("test\VictimApp.exe")
$files = Get-ChildItem -Path $root -Recurse -File -Force | Where-Object {
    $rel = $_.FullName.Substring($root.Length)          # 形如 "\test\VictimApp.exe"
    $key = $rel.TrimStart("\")
    $keep = $true
    foreach ($d in $skipDirs) { if ($rel.StartsWith($d + "\")) { $keep = $false } }
    if ($skipExact -contains $key) { $keep = $false }
    if ($key -like "test\*.json" -or $key -like "test\*.txt" -or $key -like "test\*.png") { $keep = $false }
    if ($_.Name -like "*.pdb" -or $_.Name -like "*.log") { $keep = $false }
    $keep
}

Write-Host ("`n准备上传 {0} 个文件：" -f $files.Count) -ForegroundColor Cyan

# ---------------------------------------------------------------- 传 blob
$tree = @()
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1).Replace("\", "/")
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName))
    $blob = Api POST "/repos/$Owner/$Repo/git/blobs" @{ content = $b64; encoding = "base64" }
    $tree += @{ path = $rel; mode = "100644"; type = "blob"; sha = $blob.sha }
    Write-Host ("   {0,-46} {1,9:N0} B" -f $rel, $f.Length)
}

# ---------------------------------------------------------------- 建提交
$newTree = Api POST "/repos/$Owner/$Repo/git/trees" @{ tree = $tree }

$msg = @"
发布 $Tag

一键关闭 Windows 后台应用：原生 C# / WinForms，零依赖，88 KB 单文件 EXE。
先礼后兵（正常退出优先，超时才强制结束），系统关键进程永不触碰。
含 37 项自动化验收测试。
"@

if ($firstPublish) {
    # 孤儿提交：内容就是上面这棵树，不带占位提交作为父节点
    $commit = Api POST "/repos/$Owner/$Repo/git/commits" @{
        message = $msg
        tree    = $newTree.sha
        parents = @()
    }
    $null = Api PATCH "/repos/$Owner/$Repo/git/refs/heads/main" @{ sha = $commit.sha; force = $true }
} else {
    $commit = Api POST "/repos/$Owner/$Repo/git/commits" @{
        message = $msg
        tree    = $newTree.sha
        parents = @($priorSha)
    }
    $null = Api PATCH "/repos/$Owner/$Repo/git/refs/heads/main" @{ sha = $commit.sha; force = $false }
}
Write-Host ("提交 {0}" -f $commit.sha.Substring(0, 8)) -ForegroundColor Green

try { $null = Api PATCH "/repos/$Owner/$Repo" @{ default_branch = "main" } } catch { }

# ---------------------------------------------------------------- 发 Release
$assetUrl = ""
if (-not $NoRelease) {
    $rel = $null
    try { $rel = Api GET "/repos/$Owner/$Repo/releases/tags/$Tag" $null } catch { }
    if (-not $rel) {
        $body = @"
## 一键关闭后台应用 $Tag

**下载下面的 EXE，双击就能用。** 免安装、零依赖（只用 Windows 自带的 .NET Framework）。

### 安全设计
- 永不触碰 Windows 关键进程（explorer、csrss、lsass、dwm、conhost、输入法……）
- 保护自身进程、自身的第二个实例，以及启动它的整条父进程链
- 只操作当前登录会话，远程桌面/其他用户会话里的程序一律不动
- 关闭前校验 PID 身份（进程名 + 启动时间），避免 PID 被系统回收后误杀
- 关不掉就如实报"拒绝访问"，绝不假装成功

### 测试
37 项自动化验收测试，全部通过。
"@
        $rel = Api POST "/repos/$Owner/$Repo/releases" @{
            tag_name         = $Tag
            target_commitish = "main"
            name             = $Tag
            body             = $body
            draft            = $false
            prerelease       = $false
        }
        Write-Host ("已创建 Release {0}" -f $Tag) -ForegroundColor Green
    } else {
        Write-Host ("Release {0} 已存在。" -f $Tag) -ForegroundColor Yellow
    }

    foreach ($old in $rel.assets) {
        $null = Api DELETE "/repos/$Owner/$Repo/releases/assets/$($old.id)" $null
        Write-Host ("  移除旧附件 " + $old.name) -ForegroundColor DarkGray
    }
    $uploadUrl = "https://uploads.github.com/repos/$Owner/$Repo/releases/$($rel.id)/assets?name=" +
                 [uri]::EscapeDataString($AssetName)
    try {
        $asset = Invoke-RestMethod -Method POST -Uri $uploadUrl -Headers $headers `
            -ContentType "application/octet-stream" -InFile $exeAbs -TimeoutSec 300
        $assetUrl = $asset.browser_download_url
        Write-Host ("已挂上附件：{0}" -f $assetUrl) -ForegroundColor Green
    } catch {
        Write-Host ("附件上传失败：" + $_.Exception.Message) -ForegroundColor Red
        Write-Host ("  （若提示文件被占用，关掉正在运行的实例后执行：publish.ps1 -AssetOnly）") -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host ("仓库地址：https://github.com/{0}/{1}" -f $Owner, $Repo) -ForegroundColor Cyan
if ($assetUrl) { Write-Host ("EXE 直链：{0}" -f $assetUrl) -ForegroundColor Cyan }
