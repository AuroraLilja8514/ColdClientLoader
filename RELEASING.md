# 便携版发布流程

仅发布 Windows x64 便携 ZIP 和 `SHA256SUMS.txt`，版本使用 `主版本.次版本.修订号`，Git 标签使用 `v版本号`。首版为 `v0.1.0`。

1. 在 Windows x64 上安装 .NET 10 SDK 和 PowerShell 7，从普通桌面终端运行 `pwsh -NoProfile -File Test.ps1 -LaunchModes`。同步、界面和模拟游戏启动测试全部通过后继续。发布回归不需要登录 Steam、运行真实游戏或修改真实存档。
2. 检查准备发布的内容并先在本地提交，只提交源码、测试、构建脚本和文档。用户配置、Steam 凭证、存档、录像、OBS 和 ColdClientLoader 二进制不进入源码或便携包。后续构建、验证和标签使用同一个提交。
3. 运行 `pwsh -NoProfile -File Release.ps1 -Version 0.1.0`。脚本在全新的 `dist/releases/v0.1.0` 中构建自包含版本，固定使用 .NET 10.0.13，保留独立 DLL 和许可证，生成 ZIP 及 SHA-256。输出目录已存在时停止，请先移走旧产物。
4. 解压 ZIP 到新目录，核对 SHA-256、版本号、许可证及包内文件；确认 GUI 可启动、`Record-Game.ps1 help` 正常输出、错误参数返回非零退出码。运行时应从解压目录加载，不能依赖本机安装的 .NET。检查不包含用户数据或调试符号。
5. 将通过验证的提交推送到 GitHub，创建指向该提交的 `v0.1.0` 标签并推送。标签发布后不移动；修订发布使用新的版本号。
6. 在 GitHub 创建该标签的 Release 草稿，写明功能、已验证范围、便携版使用方法和外部组件要求。上传 ZIP 与 `SHA256SUMS.txt`，核对附件名称、大小及服务端 SHA-256 后再正式发布。GitHub 自动生成的源码附件不能替代便携 ZIP。

下一版重复相同步骤并替换版本号。打包脚本不上传文件，也不读取 `%LOCALAPPDATA%/ColdClientManager`。OBS 按需下载或接入现有安装，ColdClientLoader 由使用者另行提供。

便携版无需安装程序或单独安装 .NET；程序数据继续保存在当前 Windows 用户目录，Steam 加密登录令牌也继续绑定该用户。升级时解压到新目录，关闭旧版本后运行新版，不复制或分发个人数据。
