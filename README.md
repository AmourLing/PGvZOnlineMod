# PGvZOnlineMod — 植物娘联机模组

《植物娘大战僵尸 HD》（PGvZ 1.3.1，Windows / Android，net6.0 + MonoGame）的多人合作联机 mod。
最多 **4 人**（1 主机 + 3 客人）共守一块草坪生存关；同步模型为
**Host 权威 + 客户端影子模拟**，卡组与阳光各自独立。不联机时游戏行为完全不变。

## 文档分工

| 文件 | 给谁看 | 说明 |
| --- | --- | --- |
| **`项目文档.md`** | 开发 / 维护 | **权威事实**：架构、状态机、30 种消息表、24 个钩子表、参数速查、排查手册、扩展指南 |
| `联机模组总体大纲.md` | 考古 | 早期设计与决策过程；其中的消息表/钩子数等数字已过期 |
| `使用说明模板.txt` | 玩家 | 说明书母本，打包时自动生成为 zip 内的 `使用说明.txt` |
| `参考.md` | 考古 | 思路源头（参考项目调研笔记） |

## 规模事实

| 项 | 值 |
| --- | --- |
| 协议版本 | v17（握手校验协议号 / 模组指纹 / 游戏版本，不一致拒连） |
| 玩家 / 客人 | 4 / 3（`NetMgr.MaxClients`） |
| 消息类型 | 30（`PacketType` 0..29），可靠事件 + 不可靠快照双通道 |
| 游戏钩子 | 24（清单见 项目文档 第 6 节） |
| 离线回归 | VerifyHost 42 项（netId + 关卡表 + 座位门闩 + 全消息往返 + 真 Lidgren 回环 + 真 detour） |
| 可选关卡 | **61 关**（游戏挑战表 100 条 − 点名排除 39 条，每条理由写得出） |
| 依赖分发 | Windows 复用游戏自带 Lidgren/Newtonsoft；Android 随模组带副本并启动时加载 |

## 30 秒上手

```bat
cd /d D:\植物大战僵尸\WP_PGVZ\my_mods\PGvZOnlineMod
dotnet build -c Release                          :: mod → bin\Release\net6.0\PGvZOnlineMod.dll
dotnet run --project VerifyHost -c Release --no-build :: 离线回归门（42 项，exit 0 = 全过）
python 打包.py                                   :: 编译 + 回归 + 打 zip 到 publish\
```

把 zip 内全部内容复制进游戏的 `mods\`（`PGvZOnlineMod.dll` 必须直接位于顶层）即完成安装；
主菜单"在线关卡"下方出现 **[联机]** 按钮即加载成功。
csproj 引用 `..\..\PlantGirlsVsZombies\LawnDLL\` 下的 Lawn / Lidgren / MonoGame /
MonoMod.RuntimeDetour / IronPython / Newtonsoft（全部 `Private=false`，运行时由游戏加载）；
本机 NuGet 组件损坏时 `打包.py` 自带降级链。

- Windows mods 目录：`%AppData%\ZBC\PlantGirlsVsZombies\mods\`
- Android mods 目录：`/storage/emulated/0/Android/data/net.pvz.pgvz.zbcteam/files/mods`
  （必须连同 `PGvZOnlineMod\` 数据目录一起复制，里面是 Android 版游戏缺的传输库）

## 已完成 / 待做

- ✅ 联机全流程：建房（端口自动回退）/ 自动搜房（广播 + /24 扫段）/ 房间准备制 / 踢人 /
  选卡门闩 / 种植·铲除转发与原生执行 / 出怪·天降阳光·产出·钉耙·预览僵尸·加速·暂停同步 /
  多光标与断线回退。Windows 双开已实测到：握手进房、准备开局、棋盘绑定、
  客户端出怪抑制、暂停/恢复、对方退出后单机继续（**未打过完整一局**，
  通关判定与结算表现仍需实跑）。
- ✅ 按人数加压僵尸血量（`ZombieHpPerExtraPlayer`，默认每人 +35%，0 关闭）+
  局内聊天（Enter / 右上角按钮 / 快捷短语，中文与 Android 软键盘同一条通路）
  ——**这两项尚未游戏内实测**，见 项目文档 第 18 节的验证状态。
- ⏳ 出怪数量按人数加压、僵尸冰冻/减速状态同步、更多游戏模式。
- 完整的已知边界与风险清单见 `项目文档.md` 第 16 节。

## 代码结构

`src/{Core,Protocol,Net,Sync,Hooks,Ui}` + `VerifyHost/`（离线回归台），
逐文件职责与行数见 `项目文档.md` 第 3 节。
