# HttpFileServer — 带 GUI 的局域网 HTTP 文件服务器

一个双击即用的 Windows 桌面程序：把任意文件夹变成局域网内的文件分享站点。手机、平板、其他电脑通过浏览器（扫码或输入网址）即可浏览、下载、上传文件。

同类调研见文末：老 HFS2 留下的"原生 GUI"空位 + 现代作品（dufs / miniserve / copyparty）的分块上传体验。

## 功能

**桌面端（WinForms）**
- 选择共享目录、一键启动/停止，端口与监听网卡可配，端口被占用时自动顺延
- 局域网地址列表（双击复制）+ 二维码，手机扫码直连
- 传输日志（上传/下载/打包，含对端 IP、大小、耗时、速度）与上传任务进度
- 系统托盘常驻：关闭最小化到托盘，右键菜单打开主页/退出
- 防火墙放行引导（一键以管理员身份添加 netsh 规则）
- 配置持久化：`%APPDATA%\HttpFileServer\config.json`

**浏览器端（手机/电脑通用，纯原生 JS，无构建步骤）**
- 目录浏览：面包屑、类型图标、排序（名称/大小/时间）、实时搜索
- 下载：单文件（支持 Range 断点续传、视频拖动播放）、多选流式打包 zip（边打包边传输，不落盘）
- 上传：按钮选择 + 拖拽（支持拖入整个文件夹递归）；大文件 4MB 分块、并发 3、断点续传（刷新页面后重选同一文件自动续传）；同名自动重命名为 `文件 (1).ext`
- 移动端响应式适配

## 运行要求

- Windows 10/11 x64
- Debug 运行需安装 .NET 10 SDK；发布的单文件 exe 自包含运行时，无需安装任何依赖

## 构建与发布

```bash
# 开发运行
dotnet run --project src/HttpFileServer

# 发布单文件 exe（输出在 src/HttpFileServer/bin/Release/net10.0-windows/win-x64/publish/）
dotnet publish src/HttpFileServer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 使用

1. 启动程序，选择要共享的文件夹，点击「启动服务」
2. 首次启动如弹出 Windows 防火墙提示请选择「允许」；或在程序内点击「添加防火墙规则」
3. 手机连同一 Wi-Fi，扫描二维码或访问界面上的局域网网址
4. 在网页中浏览/下载文件，或拖拽文件（可整个文件夹）到页面上传

上传的临时分块存放在 `%TEMP%\HttpFileServer\uploads\`，完成后自动合并删除，空闲 24 小时的残留会话会被定期清理。

## 技术栈

- C# / .NET 10（LTS），WinForms + 内嵌 ASP.NET Core Kestrel（`FrameworkReference`，单进程双线程模型）
- 下载走 `StaticFiles + PhysicalFileProvider`（自带 Range/ETag/Last-Modified），路径锁定共享根目录防穿越
- 上传协议：`POST /api/upload/init`（按 文件名+大小+修改时间 指纹幂等恢复会话）→ `PUT /api/upload/chunk/{id}/{index}` → `POST /api/upload/complete`（校验分块齐全、流式合并、核对总大小）
- 第三方依赖仅 `QRCoder` 一个包

## 同类作品调研（2026-09）

| 作品 | 形态 | 借鉴点 |
|---|---|---|
| HFS 2/3 (rejetto) | HFS2 原生 GUI / HFS3 Node.js 重写 | GUI + 托盘形态；流式 zip 打包 |
| filebrowser | Go CLI + Web UI | 账号体系（本工具 MVP 未做鉴权） |
| dufs / miniserve | Rust CLI 单文件 | 极简、二维码、上传 |
| copyparty | Python 单文件 | 分块可恢复上传、多协议 |
| LocalSend | Flutter GUI | "GUI 优先"在 Windows 端的真实需求 |

差异化定位：**原生桌面 GUI 壳（托盘、扫码）+ 手机友好的现代 Web 页 + 分块续传上传**，纯局域网、零配置、免鉴权。
