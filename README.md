# Workbench Testing

独立构建的 Project Workbench 测试插件。当前迁移版本为 **0.2.0**，最低宿主 **0.4.0**，SDK/API 与运行服务身份保持 **1**。需求和规范化 Figma 来源、可追溯用例、手动审核、CSV、SIMULATED 虚拟执行及受控 OwnedLab 原生目标均保留原有边界。

## 仓库归属

- 本仓：`Workbench.Testing`、`Workbench.Testing.Domain`、`Workbench.Testing.Windows`、原生 `Workbench.OwnedLab`、测试用例示例、业务测试与验收夹具、独立包和来源核验。
- 宿主 `project-workbench`：SDK、Core 测试服务和存储实现、宿主 UI、宿主契约/兼容性测试。它们未复制进本仓。
- 兼容基线：宿主提交 `655d7b6496067daad7c5e4cefb1f1da1b8379536`，SDK `0.4.0`，.NET SDK `10.0.403`，Windows App SDK `1.8.260921001`。每个迁入文件的原路径、长度及 SHA256 位于 `eng/migration-source.json`；SDK 源码锁定位于 `eng/host-sdk.lock.json`。
- 后续补入宿主 `b1a3e9c9963c2d75798a6068f99a6270d81bd074` 的源码验收释放夹具、业务回归及报告校验，来源文件和哈希记录在 `eng/source-acceptance-supplement.json` 和 `eng/source-report-supplement.json`；这不改变宿主 SDK 的固定基线。生产包的托管依赖审计按双仓库不同提交适配，来源见 `eng/managed-audit-source.json`。合成报告校验不能替代实际 Windows 原生源码验收；拆仓后验收适配及验证仍待完成，见 `docs/source-acceptance-cross-repository.md`。

宿主原文件暂时保留，待独立分支 CI 和消费端切换验证后再单独删除。目录迁入不代表 Windows 原生验收已通过。迁移没有创建新的业务能力，也没有把宿主全部测试搬走。

## 准备固定 SDK

先在 `.host/project-workbench` 放置该提交的干净宿主 checkout。也可以指定已有 checkout：

```sh
python scripts/prepare-host-sdk.py --host-root /absolute/path/to/project-workbench --reference-only
dotnet test Workbench.Testing.Core.slnx -c Release -p:HostSourceRoot=/absolute/path/to/project-workbench
```

脚本先核对宿主 HEAD 和全部 SDK 源文件 SHA256，再从允许列表生成两个本地 NuGet 包。插件使用精确 `0.4.0` 包引用，NuGet 源映射禁止从公共源替换同名 SDK。每次准备 SDK 都清除仓库私有缓存中的旧 SDK 身份，构建使用固定 `artifacts/nuget-packages`，打包前逐一核对实际消费的包和 DLL 字节。生成的包哈希写入 `artifacts/host-sdk/manifest.json`。Linux 必须显式 `--reference-only`，其输出不能当作 Windows 交付。Core 仅用于业务测试夹具；为兼容固定宿主的测试可见性，测试程序集保留历史名称 `Workbench.Core.Tests`，但本仓不运行宿主契约测试。

## Windows 独立包

需要 Windows x64、.NET 10 SDK、PowerShell 7.6+（运行在 .NET 10+）、MSVC x64 C++ 工具和 Windows SDK。在默认 `.host` checkout 布局下：

```powershell
python scripts/prepare-host-sdk.py
dotnet publish .host/project-workbench/src/Workbench.App/Workbench.App.csproj -c Release -r win-x64 --self-contained true -o .host/project-workbench/artifacts/native/app -p:SourceRevisionId=655d7b6496067daad7c5e4cefb1f1da1b8379536
pwsh -File scripts/package-testing-plugin.ps1
```

打包要求本仓工作树干净，并实际构建 MSVC helper。输出 `artifacts/native/testing-plugin/`，含内层 `workbench-testing-plugin-0.2.0.zip`、说明、`provenance.json` 与 `SHA256SUMS`。来源清单分别记录插件提交、宿主提交、SDK 包哈希、helper 身份和 ZIP/每个文件的 SHA256；八文件 ZIP 不包含 SDK、Core、WinUI、.NET 或用户数据。

公开 Actions 对独立分支 push、PR 和手动运行执行 Linux/Windows 业务测试、参考编译、实际 Windows 独立打包及受控原生探针。只有该提交的整个 Windows job 成功，才上传可安装包。失败时仍保留可用验证报告，不能把参考编译说成原生通过。

## 验证与安全边界

```sh
python -m unittest discover -s tests/migration -v
dotnet test Workbench.Testing.Core.slnx -c Release
```

所有自动验证只使用隔离合成项目、内存 HTTP 和合成凭据，不使用真实 Webhook、钉钉、用户目录或真实业务服务。真实 Figma/OpenAI 控件需用户明确发起并人工审核；自动测试不代表真实 provider 验收。Windows 11 的选择器、IME、DPI、可访问性等人工检查仍单独待验。

- [迁移后验证边界](docs/windows-verification.md)
- [插件安装与操作](docs/testing-plugin.md)
- [Figma 只读导入](docs/figma-readonly-capture.md)
- [宿主测试服务契约](https://github.com/ThaoKeeton/project-workbench/blob/655d7b6496067daad7c5e4cefb1f1da1b8379536/docs/testing-services.md)
