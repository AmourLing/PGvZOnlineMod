# PGvZOnlineMod — 植物娘联机模组

《植物娘大战僵尸》（PGvZ，net6.0 + MonoGame）的双人局域网合作 mod。
总体设计见 [`联机模组总体大纲.md`](./联机模组总体大纲.md)，思路源头见 [`参考.md`](./参考.md)。

## 当前状态（2026-09-25 首轮施工完成）

| 里程碑 | 状态 |
| --- | --- |
| M0 调研复核（Hook 目标/字段/调用链逐个对源码） | ✅ 完成（记录在大纲 1.6 节） |
| M1 骨架：mod 加载 + 日志/配置 + Lidgren 连接 + 握手 + 主菜单 [联机] + 大厅/房间面板 | ✅ 代码完成 |
| M2 核心：快照采集/应用（20Hz，卡槽/阳光/波次/实体 hp+位姿）+ Spawn/Retire 事件 + netId 注册表 | ✅ 代码完成 |
| M3 输入：种植（合成光标走原生 MouseUpWithPlant 链）/铲除/收阳光 转发与执行 + 出怪权威抑制 | ✅ 代码完成 |
| VerifyHost 离线回归（协议全编解码 + Lidgren 真回环 + HookEndpointManager 真 detour 触发） | ✅ 26/26 通过 |
| 游戏内实测（钩子真实绑定、UI 布局、手感调参） | ⏳ 待做（见下"下一步"） |

## 架构（v1 同步模型：Host 权威 + 客户端影子模拟）

```
mods/PGvZOnlineMod.dll（纯 C#，[assembly: PythonModule] 入口）
├─ Core/      ModEnv(日志·配置) / MainThreadQueue(网络→主线程) / NetIdRegistry(netId↔对象)
├─ Protocol/  13 种消息 + 编解码（Lawn 无关，VerifyHost 可离线测）
├─ Net/       NetMgr：Lidgren NetServer/NetClient 轮询封装；可靠(事件)/不可靠(快照)双通道
├─ Sync/      Session(状态机·快照编排·局域网搜房) / InputExecutor(远端输入→原生调用)
├─ Hooks/     HookEndpointManager 钩子 ×10（唯一接触游戏内部的层）
└─ Ui/        OnlineLobbyScreen(联机整页·在线关卡同款页面模式) / Hud(远端光标·状态角标)
```

- **Host**：正常跑逻辑；20Hz 采集快照（阳光/波次/卡槽冷却/僵尸位姿血量/植物血量）不可靠下行；
  新实体懒分配 netId 后 SpawnBatch 可靠下行；退场发 Retire；远端输入经 `InputExecutor`
  以"合成光标 + MouseUpWithPlant"走游戏原生种植链（校验/融合/扣阳光/冷却全复用）。
- **卡组/阳光独立（协议 v3）**：双方各选各的卡、各花各的阳光、各收各的阳光
  （收阳光纯本地，不转发）；就绪门闩挂在 `CutScene.EndSeedChooser`
  （双方完成选卡才同时进战场），Ready 包互相声明卡组；
  植物产出节奏由 Host 的 `SunProduced(netId, 重掷计数)` 事件对齐（Client 未同步的植物
  冻结计数，收到事件后取 `min(剩余, 重掷值 − RTT/2 补偿)` 自然倒数——产出动画与阳光
  出现时刻两侧对齐）；天降阳光同为事件驱动（`SkySun`：同刻同位置同类型）；
  开场预览僵尸镜像（`CutsceneZombie`）、波次倒计时随快照同步（`mZombieCountDown`）、
  加速倍率同步（`Acceleration`）。
- **Client**：抑制本地出怪（`Board/Challenge.SpawnZombieWave` 跳过）、种植/收阳光改发请求；
  僵尸/植物只由 Spawn 事件创建；快照插值校正位姿、hp 硬覆盖（≤0 补刀死亡）；
  行走/动画/子弹/音效全本地跑。断线自动回退单机（抑制自动解除，不卡死）。
- **传输**：Lidgren。Windows 用游戏自带的 `Lidgren.Network.Core.dll`（`Lawn.deps.json` 依赖）；
  **Android 版游戏不带这两个库**，故 Lidgren/Newtonsoft 随模组分发到 `mods/PGvZOnlineMod/`，
  模组启动时按程序集简单名探测（Windows 命中游戏已加载的同一份，零冲突）、失败则
  `Assembly.LoadFrom` 自带副本（`Core/NetAsm.cs`）。两平台同一套 DLL，跨平台联机互通。
- **Android 兼容补丁**：Android 的 Mono 网卡枚举可能为空，Lidgren 的
  `NetUtility.GetMacAddressBytes`/`GetBroadcastAddress` 会返回 null 导致 peer 启动即崩
  （InitializeNetwork NRE）/自动搜房失效（IPEndPoint(null)）。模组启动时用 MonoMod
  给这两个方法打空安全补丁（失败回退固定字节 / 255.255.255.255 广播），Windows 上原样透传。
- **局域网搜房**：联机页停留时 Client 每秒 UDP 广播发现请求（Lidgren `DiscoverLocalPeers`），
  处于"等待加入"的 Host 回房间信标（昵称+关卡），页面列表 1 秒内出现、点击即加入；
  广播被路由器 AP 隔离挡掉时回退手填 IP。

## 构建 / 验证 / 打包

```bat
cd /d D:\植物大战僵尸\WP_PGVZ\my_mods\PGvZOnlineMod
dotnet build -c Release                 &:: mod → bin\Release\net6.0\PGvZOnlineMod.dll
dotnet run --project VerifyHost -c Release   &:: 离线回归门（26 项，exit 0 = 全过）
python 打包.py                          &:: 编译 + 回归 + 打 zip 到 publish\
```

csproj 引用游戏目录 `..\..\PlantGirlsVsZombies\LawnDLL\` 下的 Lawn / Lidgren / MonoGame /
MonoMod.RuntimeDetour / IronPython / Newtonsoft（全部 `Private=false`，运行时游戏已加载）。
本机 NuGet 损坏时 `打包.py` 自带降级链（跳过 restore 相关目标）。

发布 zip 结构（**不带 mods/ 包装层**，把 zip 内全部内容复制进 mods\ 即完成安装，两平台通用）：

```
PGvZOnlineMod.dll                     → mods\PGvZOnlineMod.dll（必须直接位于 mods\ 顶层）
PGvZOnlineMod/联机配置.json           → mods\PGvZOnlineMod\联机配置.json（默认配置，可记事本编辑）
PGvZOnlineMod/Lidgren.Network.Core.dll → 传输库（Windows 复用游戏自带份；Android 由模组自动加载）
PGvZOnlineMod/Newtonsoft.Json.dll      → 同上（Android 版游戏不带）
使用说明.txt
```

mod 首次加载时若数据目录没有配置文件，会自动写一份默认 `联机配置.json`
（昵称/端口/上次 IP/快照频率，见 `配置模板/联机配置.json`），随后进游戏自动重读。
Android 注意：软键盘对自绘输入框的按键事件支持取决于游戏引擎，IP 手输可能不可用——
局域网自动搜房不受影响，是 Android 端的主路径。

## 部署

复制 `bin\Release\net6.0\PGvZOnlineMod.dll` 到 `%AppData%\ZBC\PlantGirlsVsZombies\mods\`，
重启游戏，主菜单左侧"在线关卡"下方出现 **[联机]** 按钮即成功（点击新开整页，
与"在线关卡"同款页面模式）。日志/配置在 `mods\PGvZOnlineMod\`。
完整玩家说明见 `使用说明模板.txt`（打包时自动进 zip）。

## 下一步（按大纲里程碑）

1. **双开实测**（M1-M3 验收）：同机双进程（127.0.0.1）过一整关生存；重点核对：
   钩子真实绑定数、[联机] 按钮布局、客户端种子卡槽被快照覆盖后的渲染、
   合成光标种植（`MouseUpWithPlant` 路径）的手感、`StatusChanged` 断线三态。
2. **体验打磨（M4）**：乐观种植回执（当前悲观等回执）、双卡槽分阳光（当前共用）、
   聊天输入 UI（协议已留包型）、暂停协商。
3. **容错与发布（M5）**：对局中途掉线的更细 UI、`IModSyncProvider` 扩展接口落地。

## 已知边界 / 风险（v1）

- 共用卡槽与阳光池（同真代 PvZ 合作）；无对战模式。
- 僵尸冰冻/减速状态不同步（走本地模拟，视觉小偏差）。
- 僵尸 hp 由快照覆盖，本地伤害链各自跑——公平性不保证，观感对齐优先。
- 钩子签名均按 1.3.1 反编译源码逐个核对（大纲 1.6 有行号），游戏升级后需按成员名复核。
- `EditWidget` 在 Lawn.dll 中是 internal，IP 输入用自绘控件实现（`IpInputWidget`，ASCII）。
