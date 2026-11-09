# 拆仓后的源码验收边界

状态：跨仓验收适配已提交到独立开发分支；本分支的 Windows Actions 结果以对应运行记录为准。合成报告测试、打包脚本或 Linux 参考编译通过，都不能单独说明四阶段原生验收已通过。

正式 SDK 仍固定在 `project-workbench` 提交 `655d7b6496067daad7c5e4cefb1f1da1b8379536`，版本 0.4.0、API 1。验收专用宿主固定在 `9d7821bddba17fd3f40958f708d1ceee2c770505`，记录于 `eng/host-acceptance.lock.json`。后者包含跨仓报告入口；脚本要求它的所有已锁 SDK 源文件与正式 SDK 基线一致，并拒绝不同提交或有未提交更改的宿主 checkout。插件仍从 `workbench-testing` 自己的干净提交构建；插件提交和宿主提交不需要相同。

验收夹具只用于隔离测试。它的来源清单与同次配对的生产包一致地记录插件、宿主和 SDK 来源、SDK 包与清单哈希、宿主程序哈希及托管依赖；宿主还会测量实际 fixture ZIP、生产 ZIP、SDK 包和宿主 SDK DLL，错配即拒绝。测试仓报告验证器独立核对实际启动进程 ID、进程出生时间、退出、租约及报告哈希，并在四个阶段分别核验同一组绑定。原同仓验收报告仍使用内部 schema 1；跨仓原生报告使用内部 schema 2。两者均不是项目数据 schema，项目数据仍为 schema 1。

Windows x64 上准备独立 checkout 的复现命令（需 .NET 10、PowerShell 7.6+、MSVC x64 和 Windows SDK；测试仓本身须干净）：

```powershell
git clone --no-checkout https://github.com/ThaoKeeton/project-workbench.git .host/project-workbench
git -C .host/project-workbench checkout --detach 655d7b6496067daad7c5e4cefb1f1da1b8379536
git clone --no-checkout https://github.com/ThaoKeeton/project-workbench.git .host/acceptance-host
git -C .host/acceptance-host checkout --detach 9d7821bddba17fd3f40958f708d1ceee2c770505
$env:WORKBENCH_DISABLE_REAL_SERVICES = 'true'
$env:WORKBENCH_HOST_PROFILE = 'source-acceptance'
$env:WORKBENCH_ACCEPTANCE_HOST_ROOT = (Resolve-Path .host/acceptance-host).Path
$env:WORKBENCH_HOST_APP_DIRECTORY = Join-Path $env:WORKBENCH_ACCEPTANCE_HOST_ROOT 'artifacts/native/app'
python scripts/verify-acceptance-host.py
python scripts/prepare-host-sdk.py
dotnet publish .host/acceptance-host/src/Workbench.App/Workbench.App.csproj -c Release -r win-x64 --self-contained true -o $env:WORKBENCH_HOST_APP_DIRECTORY -p:SourceRevisionId=9d7821bddba17fd3f40958f708d1ceee2c770505
./scripts/package-testing-source-fixture.ps1
./scripts/source-acceptance-cross-probe.ps1
```

脚本要求在验收宿主的自有 `artifacts/native` 目录中创建全新的夹具和配对包目录；已存在时拒绝覆盖。四个阶段依次为 `seed`、`verify`、`stop-figma`、`stop-ai`，合计 36 项阶段检查；前一阶段失败不会启动后续阶段。输出位于 `artifacts/native/source-acceptance-probes/`。公开 Actions 的 `source-cross-windows` 作业使用隔离 Windows Server runner 构建，并保存同次报告、两个 ZIP、SDK NuGet 包与清单，以及四个被哈希绑定的宿主可执行文件和程序集，便于重新核对字节。

这里的 Figma/OpenAI 响应为进程内合成数据，不连接真实服务、Webhook、钉钉或用户设备。自动 Windows Server 结果也不代替 Windows 11 上真实用户的选择器、输入法、DPI、可访问性和设备交互验收；局域网和远程桌面能力另见 [后续架构](future-distributed-testing.md)。
