# FH Relevel — 霜港迷城「重新升级」修改器

[![Game](https://img.shields.io/badge/Game-Frosthaven-blue)](https://store.steampowered.com/app/2347080/)
[![Mod Loader](https://img.shields.io/badge/Mod%20Loader-BepInEx%205-orange)](https://bepinex.github.io/bepinex_docs/master/index.html)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

给《霜港迷城》（Frosthaven, Steam 版）加上幽港迷城（Gloomhaven）公会模式同款的
**「重新升级」**功能：点一下，返还角色的全部**选卡点**与**专精点**，移除已选的
2 级以上技能卡与专精——之后照常重新升级、重新做二选一。

## 功能

- **保留**：XP 与等级进度、金钱、装备、卡牌强化、精通、个人任务
- **返还**：全部选卡点（CardUnlocks）与全部专精点（PerkMarks），攻击修正牌堆重算
- 两个入口：
  - **F8 面板**：进入战役后按住 F8（或 F9）半秒，左侧弹出面板，每个角色一行
    （名字 / 职业 / 等级 / XP / 高级卡数 / 专精数），点「重新升级」即可
  - **新卡牌界面按钮**：打开角色的「新卡牌（升级二选一）」界面时，屏幕右上角出现
    「↺ 重新升级」小按钮 → 点击 → 游戏原生确认框 → 确认后界面刷新直接重选
- 重置后自动存档；载入战役时面板自动展示 15 秒作提示
- **多人游戏**：房主操作，一键把重置结果同步给全队（详见下文）
- 战斗场景中自动禁用（回到前哨/大地图再用）

## 安装（普通用户）

1. 到 [Releases](../../releases) 下载最新的 `FHRelevel_x64.zip`
2. 解压后运行 `install.bat`（会自动探测游戏目录，探测不到就手动输入）
   ——或者手动把压缩包内除 `install.bat` 外的所有文件复制到游戏根目录
   （`Frosthaven.exe` 所在目录，例如 `C:\Program Files (x86)\Steam\steamapps\common\Frosthaven`）
3. 启动游戏。**启动时会弹出黑色 BepInEx 控制台窗口**——这是修改器加载成功的标志
4. 进战役，按住 F8 半秒

卸载：删除游戏目录下的 `BepInEx`、`winhttp.dll`、`doorstop_config.ini`、
`.doorstop_version` 即可，不影响存档。

## 多人游戏用法

**原则：只有房主装、只有房主操作，其他人什么都不用做。**

1. 房主按单人流程操作（F8 面板或新卡牌按钮）
2. 重置完成后，修改器自动把新的战役状态广播给所有联机玩家（走游戏自带的
   `net_sync_state` 同款全量同步机制），大家的界面自动刷新、看到返还结果
3. 存档只由房主写入，不会冲突
4. 客户端（加入房间的其他玩家）打开面板会看到"由房主操作"的提示

## 从源码构建

依赖 .NET 8 SDK：

```bash
cd plugin
# GameDir 指向你的游戏目录
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Frosthaven"
```

产物 `plugin/bin/Release/FHRelevel.dll` 放进游戏目录的 `BepInEx\plugins\`。

## 常见问题

- **按 F8 没反应 / 没有黑色控制台**：说明 BepInEx 没加载成功。退出游戏，
  **等 30 秒以上**再启动（刚退出立即重启会因文件占用导致注入失败）。
  启动时黑色控制台弹出 = 加载成功。
- **按钮/面板没出现**：确认已进入战役（前哨或大地图），战斗场景中功能禁用。
- **诊断日志**：`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Frosthaven\FHRelevel\probe.log`
  记录了插件全程运行状态，报 issue 时请附上。
- **改热键**：游戏目录 `BepInEx\config\bishi.fh.relevel.cfg`。

## 免责声明

本修改器仅供单人/好友联机学习交流使用，请勿在竞技或商业场合使用。使用前请自行备份
存档（存档位于 `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Frosthaven\Steam\`）。
与 Snapshot Games / Flaming Fowl Studios 无关联。

## 技术备注

霜港迷城的运行时对 mod 相当不友好，本插件绕过了以下限制（详见提交历史）：

- 游戏程序集 AOT 预编译 → Harmony 补丁无效，界面检测改用轮询
- 游戏会销毁外来 MonoBehaviour 对象（BepInEx 宿主与自建对象均会被杀）
  → 全部逻辑跑在静态事件与纯 C# 类上
- IMGUI（OnGUI）不渲染 → 使用游戏自己的 uGUI 体系
- ScreenSpaceOverlay 画布被渲染管线跳过 → 挂接游戏 UI 相机（ScreenSpaceCamera）
- Unity「假 null」语义陷阱 → 对插件实例的判空使用 `ReferenceEquals`

## License

[MIT](LICENSE)（BepInEx 本体遵循其自身 LGPL 许可）
