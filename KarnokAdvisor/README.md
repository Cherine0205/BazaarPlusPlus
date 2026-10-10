# 卡诺克旅途参谋

独立的 Windows BepInEx 插件，与 BazaarPlusPlus 一起加载。读取当前卡诺克对局，比较事件、升级、买卡和换卡选项，显示推荐理由、操作顺序及尚未量化的因素。插件只提供建议，选择由玩家完成。

推荐面板采用深色皮革、暖金边框与卡牌排版，首选默认展开，备选和待确认因素可展开查看。拖动标题栏移动面板，长内容可滚动；F9 显示或隐藏，F10 刷新。

## 本机配置与安装

需要已安装的 The Bazaar、BepInEx、Python 3.10 或更新版本和 .NET SDK。Python 部分只使用标准库；构建时引用本机游戏程序集，仓库不分发游戏程序集或数据库。

首次使用时，在本目录复制配置模板：

```powershell
Copy-Item .\settings.example.json .\settings.json
```

`settings.example.json` 列出支持的配置项。缓存目录留空时，使用当前 Windows 用户的游戏缓存。模型接口默认关闭，未配置时使用本地规则估算。启用模型后，将对局状态和候选方案发送给配置的服务进行排序；模型结果仍受本地可执行性校验约束。密钥通过配置所指定的环境变量或本机文件提供，配置和密钥文件保留在本地。

`model_api.protocol` 支持 `openai`（Chat Completions）和 `gemini`（原生 `streamGenerateContent`）。前者的 `base_url` 指向版本根路径，例如 `/v1`，可通过 `stream` 启用流式；后者例如 `/v1beta`，使用原生流式响应。模型名称填写服务商提供的模型 ID。两种协议均使用 Bearer 认证，适用于 Openlux 这类网关。流式结果完整结束且通过校验后才应用到面板；失败时继续使用本地规则。

关闭游戏后，构建并安装。通过脚本参数指定本机的游戏目录和 Python 路径：

```powershell
.\tools\build.ps1 -GameDirectory 'D:\SteamLibrary\steamapps\common\The Bazaar'
.\tools\install.ps1 -GameDirectory 'D:\SteamLibrary\steamapps\common\The Bazaar' -PythonExecutable 'D:\miniconda\python.exe'
```

通过 Steam 启动游戏。安装脚本会为独立插件写入当前源码、Python 和运行目录的位置；移动目录或更换 Python 后，重新运行安装脚本。移除插件使用 `tools/uninstall.ps1`。

## 验证与边界

在本目录执行：

```powershell
python -m unittest discover -s tests -v
```

部分检查需要本机游戏缓存和 `settings.json`，Windows 文件共享检查在其他系统跳过。界面、快捷键及实时数据传递需要进入实际游戏验证。

本地规则给出启发式估算。未知事件效果和未展示的后续商品会注明不确定性；建议不代表已计算出最优策略。
