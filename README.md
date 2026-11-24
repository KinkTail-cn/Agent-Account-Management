# agent账号切换器

在 Windows 桌面管理账号、分组和 2FA 验证码，快速切换 ChatGPT、Claude 和 Antigravity 的本地登录环境。

## 下载与更新

1. 首次使用时，下载 `Agent-Account-Management-windows-x64.zip`，解压到自己的文件夹，双击 **agent账号切换器.exe**。[GitHub Releases](https://github.com/KinkTail-cn/Agent-Account-Management/releases)
2. 软件每天启动时检查一次正式版。也可以点击左下角「软件更新 → 检查更新」。发现新版后，点「下载更新」，校验完成再点「重启并更新」。
3. 使用 1.2.1 或更早版本时，先退出旧程序，解压并打开首个带更新功能的版本。新程序会读取现有账号库和登录环境。

支持 64 位 Windows 10 和 Windows 11，需要 Microsoft Edge WebView2 Evergreen 运行时。首次使用 Antigravity 时，如果软件提示缺少官方 CLI，可双击压缩包内的 `Install-Antigravity-CLI.cmd` 下载并校验组件。

## 添加和管理账号

- 点击右上角的「添加账号」，填写名称和登录账号。密码、网站、备注和 2FA 均可选填。
- 在「所属分组」选择现有分组，或直接新建。以后可以在账号详情里点击「编辑账号」移动分组。
- 点击账号即可查看详情、显示密码或复制字段。点亮星标后，可从「我的收藏」快速访问。
- 点击首页分组卡片上的「编辑」，或侧栏分组旁的铅笔，即可修改名称、图标和颜色。展开「组内账号」可一次调整多个账号，点击「保存分组」后生效。取消勾选的本组账号会移入「未分组」。
- 在「管理分组」中可以删除分组，里面的账号会移入「未分组」。

## 快速切换 Agent 账号

从侧栏进入「Agent 切换」。先选 ChatGPT、Claude 或 Antigravity，再点「添加切换账号」或「绑定已有账号」。选择本机客户端后保存。每个账号和客户端的组合都有独立的登录目录。

- **ChatGPT** 支持新版 Windows ChatGPT 桌面应用、Codex CLI 和 ChatGPT 网页窗口。
- **Claude** 支持 Claude Code 和 Claude 网页窗口。
- **Antigravity** 支持 Antigravity 桌面应用和 Antigravity IDE。

点击「首次打开」并在官方界面完成登录。终端客户端也提供「登录」按钮。以后点击「切换到账号」就会打开该账号的环境。服务商要求重新验证时，在打开的客户端完成即可。使用特定项目的账号可以在编辑页选择项目文件夹。

Antigravity 的「首次登录」会打开这条记录专属的 Google 账号选择页。复制 Google 页面显示的一次性授权码，回到切换器展开「Google 页面显示了授权码」，粘贴后点击「提交给 Antigravity」。窗口会显示验证进度。通过后点击「打开 Antigravity」。如果请求超时或 Google 拒绝了旧码，点「重新获取授权码」，从新页面复制新码。已有记录曾打开了其他账号时，可点「重新登录」。新授权成功后才会切换已有登录环境。

登录状态由客户端保存在 `%LOCALAPPDATA%\KeyIsle\AgentProfiles` 下，每个账号由固定标识区分，改名或修改分组后仍使用原来的目录。切换器显示的是客户端打开时间，实际登录身份以客户端的账号菜单为准。删除账号库记录会保留客户端资料，可先在对应客户端退出登录。

实现参考了开源切换器的独立配置目录设计。[ChatGPT Switcher](https://github.com/kociden/chatgpt-switcher) · [codex-multiple-login](https://github.com/IvanescuChristian/codex-multiple-login) · [claudenv](https://github.com/bodasooqa/claudenv) · [Claude Code 多账号登录](https://code.claude.com/docs/en/authentication#log-in-with-multiple-accounts)

## 账号状态与模型额度

「Agent 切换」和账号详情会显示该登录环境的状态。点击「刷新」重新检测，展开额度行查看各个模型或共享周期的剩余百分比、重置时间和数据来源。旧数据会标注记录时间。到了重置时间，该周期显示「待更新」。

- **ChatGPT 桌面版和 Codex CLI** 通过该账号的 Codex 配置查询登录状态与 Codex 订阅额度。若客户端返回邮箱，切换器会与账号记录中的邮箱核对。[Codex 额度接口](https://developers.openai.com/codex/app-server)
- **Claude Code** 通过官方 CLI 确认登录状态。从切换器重新打开终端并正常使用后，支持的订阅会通过状态栏回传额度。5 小时和 7 天共享额度按服务商返回的周期显示。[Claude Code 状态栏](https://code.claude.com/docs/en/statusline)
- **Antigravity** 完成首次登录后点击「刷新」，即可按模型组查看额度和重置时间。「额度已授权」表示用量查询授权有效。如果桌面客户端把当前邮箱显示在账号菜单中，展开菜单后刷新可以核对该窗口的身份。[Antigravity 用量报告](https://antigravity.google/docs/cli/commands/usage)
- **网页窗口** 点击「官方用量页」，待数字显示后保持该窗口打开，在切换器点击「刷新」。也可以复制用量页文字，点击「摘录用量」保存。页面摘录会标明手动来源。

普通账号和 Agent 账号都可在「编辑账号 → 账号状态」手动标记为正常、异常或停用。原有记录默认显示为未标记。

## 查看 2FA 验证码

1. 在网站的双重认证设置中选择验证器应用，取得设置密钥或 `otpauth://totp/` 链接。
2. 将其粘贴到切换器的「2FA 验证器密钥」，保存账号。首次在网站启用 2FA 时，将切换器生成的验证码填回网站以完成绑定。
3. 从账号详情或左侧「2FA 验证码」查看当前验证码。圆环显示剩余秒数，到期自动换码。点击复制按钮即可复制完整数字。

普通 Base32 密钥默认生成 6 位验证码，每 30 秒更新。设置链接可指定 SHA-1、SHA-256 或 SHA-512、6 至 8 位数字与换码周期。验证码依据电脑的系统时间离线计算。

## 外观与快捷键

左下角的设置按钮可更改浅色或深色外观、玻璃浓度与动态效果。默认外观跟随系统，也会遵循系统的减少动画偏好。

| 操作 | 快捷键 |
| --- | --- |
| 添加账号 | `Ctrl + N` |
| 搜索 | `Ctrl + K` 或 `Ctrl + F` |
| 保存当前表单 | `Ctrl + Enter` |
| 关闭面板或清除搜索 | `Esc` |

## 本地数据

账号、密码及 2FA 密钥使用 Windows 数据保护 API（DPAPI）加密，保存在 `%LOCALAPPDATA%\KeyIsle\vault.dat`，绑定当前 Windows 用户。更新或移动可执行文件后，以同一 Windows 用户打开即可继续使用原账号库。

复制的密码最多保留 30 秒，验证码会在 30 秒或本周期结束时清除，以先到者为准。应用只会清除自己复制且仍留在剪贴板中的那一项。

## 从源码构建

在本目录运行 `python build.py`，生成的 `agent账号切换器.exe` 包含界面、字体、插画与宿主依赖。构建使用 64 位 Windows、Python 3、系统的 .NET Framework 4.8 编译器与 `curl.exe`。首次构建会获取并校验指定版本的微软 WebView2 SDK。

运行 `python tools/package_release.py` 生成便携版 ZIP 和 SHA-256 校验文件，输出目录为 `release`。发布新版时，修改 `native/Program.cs` 里的程序集版本，提交代码，再推送对应的 `v主版本.次版本.修订号` 标签。GitHub Actions 会构建并发布正式版，应用会从该仓库的 Releases 检查更新。

Antigravity 用量查询可使用已安装的官方 `agy.exe`，也可运行 `python tools/install_quota_cli.py` 将经过 Google 发布校验值核对的 Windows x64 CLI 安装到切换器自己的工具目录。

`tests/TotpVectors.cs` 包含 RFC 6238 标准向量与输入边界测试。安装 Python 版 Playwright 后，运行 `python tests/desktop_smoke.py` 可用独立的临时账号库验证桌面操作、验证码刷新和重启恢复。字体、图标与 WebView2 许可见 `licenses` 目录。
