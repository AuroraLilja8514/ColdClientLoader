ColdClientManager · 游戏存档同步与非 Steam 环境启动（Windows x64 原型）

GitHub Release 便携版：解压整个 ZIP，运行 ColdClientManager\ColdClientManager.exe；已包含 .NET 运行时，无需安装。
源码通过 Build.ps1 构建的版本位于 dist\ColdClientManager\ColdClientManager.exe，需要 .NET 10 Windows Desktop Runtime（x64）。
请从资源管理器运行，避免继承 Codex 等打包应用的存档目录虚拟化环境。
便携版的配置、登录和备份仍保存在下文的用户目录；更新时解压到新的程序目录即可复用，不要随发布包分享用户数据。
不需要安装 Steam、Procmon、Sandboxie 或 Windows Performance Toolkit。

使用方法
1. 主页面显示已保存的应用。已有配置会直接出现在列表中，无需重新填写；选择条目后可直接同步或启动。
   点击“添加应用”打开设置窗口，填写名称、选择游戏 exe。运行目录默认为 exe 所在目录，可以填写启动参数。
2. 在设置窗口选择“直接启动”或“ColdClientLoader”。后者还需选择现有的 loader exe，其同目录应有 steamclient.dll、steamclient64.dll，建议也有两个 GameOverlayRenderer DLL。
   启动器的 x86/x64 架构应与游戏相符。Steam 安装清单可读时，会尝试填入 AppID、SteamID 和名称；否则手动填写。
   SteamID 应与原存档使用的账号一致，避免游戏按账号切换到另一个存档目录。使用 ColdClientLoader 前先退出其他 Steam / loader。
   本应用不附带 ColdClientLoader，请选择自己已有组件目录中的 steamclient_loader_x64.exe 或对应的 x86 版本。
3. 可在设置窗口点击“保存并查找存档”，先保存条目再启动游戏发现存档。ETW 采集器需要一次 UAC；游戏仍按本应用当前的普通用户权限运行。
   在游戏中产生一次进度并正常退出。应用会等待整个进程树退出，再展示候选目录。
4. 候选目录默认全部不勾选。勾选确认的目录后，点击主页面“编辑设置”，为新增规则填写“同步目标路径”。
   也可以手动添加单文件或目录。目录递归同步，可用 *.sav;*.dat 筛选文件；* 表示该目录内所有文件。
   例如：本地 C:\Users\你\AppData\Roaming\某游戏\saves
   对应同步目标 E:\SharedSaves\某游戏\saves。
   两侧填写“对应目录本身”，不会再自动附加一层本机目录名。
5. 点击“保存”返回主页面，设置会按条目保存，应用重启后仍可选择。取消添加不会创建条目；取消编辑会保留原设置。
   主页面提供三种操作：
   - 同步并启动：启动前从同步目标拉取到本机；等待全部游戏子进程退出，再将本机存档复制回同步目标。
   - 仅同步：双向比较选定规则内的存档，不启动应用；两侧冲突时手动选择保留内容。
   - 仅启动：启动前与退出后均不同步存档，也不创建存档同步基准或备份。
   首次可使用“仅同步”检查两侧差异。Steam 成就和状态 / 时长仍按该条目保存的选项执行，与存档文件同步独立。
   只有被加入规则的路径会同步。应用不会自动把其他写入目录加入同步规则。

同步规则
- 自动“同步并启动”使用固定方向：启动前以同步目标为源复制到本机，退出后以本机为源复制到同步目标；两边不同也按该方向覆盖，并保留被覆盖内容的备份。
- 手动“仅同步”比较文件内容的 SHA-256，并使用本地上次成功同步的内容作为基准，判断哪一侧改变。
- 手动同步首次两侧不同，或两侧都改变且内容不同，会要求逐个选择保留哪一侧；取消则停止本次手动同步。
- 不传播删除。固定方向复制只处理源位置实际存在且符合筛选的文件，目标端额外文件保留；手动双向同步会补回一侧缺失的文件。
- 覆盖前保留原文件备份。复制先写临时文件，校验后替换；同步预览之后文件变化会阻止使用旧计划。
- 按文件提交，不是多个存档文件组成的一次完整事务。中途断电或磁盘报错可能只完成部分文件，保留备份，重新检查后再同步。
- 不跟随链接 / 联接点；两侧及不同规则不能互相重叠。请选择链接指向的实际目录。
- 配置、基准、备份、采集报告在本地 %LOCALAPPDATA%\ColdClientManager。
  请保留每个应用实例各自的基准，不要把整个配置目录作为存档同步。
  多个实例可以使用同一个共享存档目录，也可直接将存档目录映射到同步目标。
- 同步目标产生的新进度，下次通过本应用启动游戏前会被同步；请先等待相关游戏退出、文件同步完成。
  本应用自身不调用 Steam 云存档接口；关闭应用后不会持续监控文件。

权限
访问选定的存档目录失败时，可为本次预览 / 同步通过 UAC 请求管理员权限，游戏不随采集器或同步助手提升权限。
本应用不递归取得目录所有权、不修改真实存档目录 ACL。管理员仍不能读取的 ACL / 加密文件会报错，需要另行处理。
请以普通用户运行主应用；如果自行把整个应用以管理员身份启动，游戏也会继承该身份。

ColdClientLoader 行为
原始组件复制到该游戏独立的应用数据目录，生成专用配置；不替换游戏的 steam_api(64).dll。
启动前保存九项 HKCU Steam 客户端注册表值，全部进程结束后恢复存在状态、内容和类型。
异常退出留下恢复记录时，下次先点“恢复启动器环境”。备份和恢复针对启动时的当前用户，不会替其他账号改注册表。
模拟器自身的缓存 / 用户数据位于专用 loader 目录；游戏真实存档仍由游戏决定。
并非每款 Steam 游戏都兼容裸启动或 ColdClientLoader；本应用不能保证任意 DRM、反作弊、外部启动器兼容。

Steam 成就同步（无需官方 Steam 客户端）
1. 点击“Steam 登录”，在本地窗口输入账号和密码，按提示输入邮件验证码或在手机 Steam 中确认。
   不保存密码。刷新令牌由 Windows 当前用户加密，存放在 %LOCALAPPDATA%\ColdClientManager\steam\login.json。
   后续操作、应用重启和测试会复用登录；令牌失效时，点击“退出 Steam 账号”后重新登录。
   加密令牌只能由当前 Windows 用户使用，不要把该文件复制给其他用户充当登录。
2. 在主页面选择应用并点击“编辑设置”，选择 ColdClientLoader，填写正确的 AppID 和存档账号 ID。登录账号必须与存档账号 ID 一致。
3. 点击“同步 Steam 成就”，下载成就定义并合并 Steam / 本地已解锁记录；本地未解锁项不会上传。
   也可在设置窗口勾选“游玩前后同步 Steam 成就”，保存后在启动前准备、退出全部游戏进程后同步。默认不勾选。
   自动同步需要网络；离线游玩时取消勾选，联网后再手动同步保留下来的本地成就。
4. 已同步的本地成就目录会绑定账号；要换账号，请新建游戏配置，避免混用记录。
成就定义写入该游戏独立的 loader\steam_settings；已解锁记录在 loader\emu-data\<AppID>\achievements.json。
同步只增加解锁，不重置成就，不上传普通游戏统计数值；尚未解锁成就的统计进度不保证与官方客户端一致。
上传后会重新读取 Steam 核实结果；服务端拒绝、数据过期、断网或本地文件错误都会显示失败，本地记录保留。
Steam 对新导入成就记录的时间可能是同步时间，不能保证保留离线取得时的原始时间。
此功能独立于存档文件同步；未实现 Steam 云存档或 DLC 管理。当前实测范围是 Spacewar（AppID 480）。
实现参考 GameNative 的“读取 Goldberg 已解锁记录、与服务端位掩码合并、提交并核实”的流程：
https://github.com/utkarshdalal/GameNative/blob/master/app/src/main/java/app/gamenative/service/SteamService.kt
网络连接和认证采用 SteamKit2 3.4.0：https://github.com/SteamRE/SteamKit/tree/3.4.0

Steam 游玩状态与时长
在应用的设置窗口填写正确的 Steam AppID 和存档账号 ID，勾选“上报 Steam 状态与时长”并保存，再登录对应账号。默认不勾选，与成就同步独立，直接启动和 ColdClientLoader 均可使用。
启动时向 Steam 上报该 AppID，设置在线状态，并等待服务器确认正在游玩；连接保持到全部游戏子进程退出，再清除游玩状态并核实。
时长由 Steam 服务器累计，账号需要拥有该游戏的有效许可；个人资料的可见范围仍受 Steam 隐私设置影响。
其他客户端正在游玩时会阻止本次启动，可以结束该游戏或取消此选项；不会主动抢占该客户端的游玩会话。
游玩中断网或 Steam 会话被占用会显示上报中断；离线时长不会自动补记。游玩期间请保留应用窗口。

OBS 游戏录制与命令行
可接入已安装的 OBS Studio 30.1+（x64），也可使用独立的 OBS Studio 32.2.2 便携实例。
两种方式均仅捕获指定游戏窗口及游戏进程音频，不加入桌面或麦克风音源。
已有 OBS：在应用“编辑设置”的“OBS 程序”中选择安装目录下的 bin\64bit\obs64.exe 并保存，无需下载便携版。
命令行在 start 后使用 --obs-exe "C:\Program Files\obs-studio\bin\64bit\obs64.exe"，无需先运行 install。
先退出其他 OBS；本应用启动和关闭自己创建的 OBS 进程，不接管已经运行的 OBS。
使用专用 ColdClientManager 配置与场景集合；已存在同名的个人配置时会报错，请先在 OBS 中改名。
录制期间临时调整共享界面设置与 WebSocket 设置，结束或启动失败后恢复原 global.ini、user.ini、WebSocket 配置及其 .bak 文件。
原有个人配置与场景不改动；临时启动仅加载 OBS 内置插件。录制期间请勿另开 OBS 或修改共享设置。
设置备份以 Windows 当前用户加密，存放于 %LOCALAPPDATA%\ColdClientManager\recording，不写入安装目录。
异常中断后先 stop；如果提示设置未恢复，退出所有 OBS 后执行 .\Record-Game.ps1 recover。未恢复前会阻止开始新的录制。

未安装 OBS 或希望使用专用便携版时，在 ColdClientManager.exe 所在目录打开普通 PowerShell，首次运行：
  .\Record-Game.ps1 install
这会下载并校验官方 Windows x64 压缩包，安装到 %LOCALAPPDATA%\ColdClientManager\obs。
已有该版本官方压缩包时，可离线安装：
  .\Record-Game.ps1 install --archive "D:\Downloads\OBS-Studio-32.2.2-Windows-x64.zip"
便携版安装文件、专用配置与会话记录保存在用户应用数据目录，不使用仓库内的测试 OBS。

在应用“编辑设置”中勾选“启动时用 OBS 录制”，可保存该游戏的录像输出目录及 MKV / MP4 格式。
默认输出到当前用户目录下 Videos\ColdClientManager，采用 720p / 30 帧、Intel QSV H.264 5000kbps、MKV。
自动录像文件名为“游戏名称_开始日期_时-分-秒-毫秒_随机标识.mkv/mp4”，例如 Order of the Sinking Star_2026-10-22_10-23-45-123_随机标识.mkv。
名称使用已保存的游戏名称，文件名不允许的字符替换为下划线；每次录制都有独立标识，同一毫秒开始也不会共用文件名，已有文件不覆盖。
命令行 --output 明确指定的完整文件名仍按原样使用。
MKV 与 MP4 均使用相同的 QSV H.264 / AAC 编码设置；格式主要影响封装和兼容性，不能靠改扩展名明显降低游戏负担。
默认推荐 MKV，异常中断时更容易保留可播放内容；需要 MP4 时可在游戏设置选择，也可结束后用 OBS 的“录像转封装”转换，无需重新编码。
“同步并启动”和“仅启动”均遵循该录制选项；全部游戏子进程退出后停止录制并正常关闭本次启动的 OBS。“仅同步”不录制。
录制启动失败会显示错误，游戏仍会继续运行；录制结束失败会保留工作录像并报错。

也可对已经运行的游戏单独使用命令行，无需从管理器重新启动。先取得进程 ID，例如：
  Get-Process sinking_star | Select-Object Id, ProcessName
将下面的 1234 替换成实际游戏进程 ID，输出位置和文件名均可自行设置：
  .\Record-Game.ps1 start --pid 1234 --output "D:\Game Videos\沉星之序.mkv" --width 1280 --height 720 --fps 30 --bitrate 5000 --encoder qsv --audio game --cursor false
  .\Record-Game.ps1 status
  .\Record-Game.ps1 stop
如需定时停止，可在 start 命令末尾加 --duration 60；命令会保持运行，到时自动停止。也可在另一终端提前 stop。
--output 是必填的完整文件路径（支持相对路径），支持 .mkv / .mp4。父目录不存在时会创建，已有文件不会覆盖。
录制中，工作录像以 CCM-随机标识.mkv/mp4 写入所选目录；正常停止后改为指定文件名。中断或重命名失败时保留工作文件。
start 命令同时可设置：--width、--height、--fps、--bitrate（kbps）、--encoder（qsv/x264）、--audio（game/none）、--cursor（true/false）、--duration（秒）、--obs-dir（专用便携 OBS 目录）、--obs-exe（已安装的 obs64.exe）。
默认值为 1280×720、30 帧、5000kbps、qsv、game、false；无 --duration 时直到 stop。选择 x264 会使用 CPU 编码，不会自动从 QSV 切换。
--obs-dir 仅接受由本应用 install 准备的专用目录；复制整个专用便携目录后也可指定新的位置。配置仍在该便携目录内。
--obs-exe 与 --obs-dir 不能同时指定。“OBS 程序”留空时，应用继续使用原有专用便携版。
同一用户同时只管理一个录制会话。CLI 使用原生 .NET WebSocket，不需要 Python、另装 OBS 插件、Steam 面板或额外的 OBS 账号。
30 帧表示录像输出频率；游戏本身不足 30 FPS 时会重复画面，不能据此判定录制编码器丢帧。wcap 的 30/60 帧上限也不等于真实产生了这么多游戏画面。
独占全屏、反作弊或不同权限的游戏不保证可捕获；捕获失败时明确报错，不会自动改录桌面。使用普通桌面终端运行这些命令。
其他命令示例及参数帮助：.\Record-Game.ps1 help

发现能力的边界
本次实际测试捕获了七进程模拟树、七个模拟存档的成功创建与写入，ETW 丢失事件为零。
发现是候选推荐，不能仅凭一次文件写入证明“这就是存档”。缓存和日志会过滤，其他模糊目录保留给用户判断。
内存映射写入、仅重命名时的最终名称，以及由进程树外既有的 Steam / 系统服务代写的文件，可能漏报。
同目录临时文件的写入也可能产生候选目录，确认时请查看目录真实内容，必要时改用准确的存档文件筛选。
Job Object 会跟踪普通子孙进程，即使启动器先退出；已存在的外部服务或主动脱离进程树的启动方式不在此保证内。
遇到这类游戏，请使用手动规则，并在确定游戏保存完毕后使用“立即同步”。
采集报告 result.json 包含进程列表、合并后的路径活动及丢失 / 未完成 / 未知进程操作计数；ProcessId 是该路径最后一次被记录的关联进程。
没有把本次文件系统测试推广为“能百分之百识别任意游戏存档”。当前版本不发现注册表存档。

构建与模拟测试（不启动真实游戏）
- 安装 .NET 10 SDK 后：pwsh -NoProfile -File Build.ps1
- 制作包含运行时的便携 ZIP：pwsh -NoProfile -File Release.ps1 -Version 0.1.0；完整发布步骤见 RELEASING.md。
- 普通测试：pwsh -NoProfile -File Test.ps1
- 应用列表三种操作的模拟测试：从普通桌面终端运行 pwsh -NoProfile -File Test.ps1 -LaunchModes
- ETW 模拟测试：pwsh -NoProfile -File Test.ps1 -Etw
- Cold 模拟测试：pwsh -NoProfile -File Test.ps1 -ColdLoader "现有的 loader.exe 完整路径"
- 临时目录权限测试：pwsh -NoProfile -File Test.ps1 -Permissions
- Steam 成就联网测试：先在应用登录，再运行 pwsh -NoProfile -File Test.ps1 -SteamAchievements -ColdLoader "原始 loader.exe 路径" -SteamApi "原版 steam_api64.dll 路径"
  该测试在独立目录复制测试程序和所选 DLL，用 AppID 480 产生并上传一项尚未解锁的 Spacewar 成就，再回读验证、重复同步。
  测试结束（包括失败退出）会撤销本次新增成就并恢复本地记录，保留原有成就和普通统计；Spacewar 全部成就已解锁时会停止测试。
  不修改所选 DLL 的原文件。断网导致撤销失败时会明确报错，需要联网后完成清理。
- Steam 状态与时长联网测试：先在应用登录，再运行 pwsh -NoProfile -File Test.ps1 -SteamPlaytime
  使用 AppID 480 和实际运行两分钟的模拟进程树，验证服务器游玩状态、退出清除、时长增加。测试会留下真实的 Spacewar 游玩时长。
  权限测试只修改 test-results 内的临时目录，并通过 UAC 完成同步和测试 ACL 恢复。
测试异常或安全软件提示时应暂停，不应自动关闭安全软件或循环重试。

当前验证报告在 test-results\verification.json；早期失败尝试的日志也保留，以区分已通过的最终测试。
先前 Can of Wormholes 测试的临时 save1.sav 及其备份已删除，原始游戏文件和采集记录保留。
