# 第三方组件与许可声明

本仓库的**自有源码**（`src/`、`VerifyHost/`、`打包.py` 等）采用 MIT 许可，见仓库根 `LICENSE`。
但宽松许可都要求"再分发时同时附带各自的版权声明与许可声明"，所以单独列这一份。

## 一、随发布包一起再分发的二进制

打包 zip 里实际带着下面两个 DLL（供 Android 版游戏使用，Windows 会复用游戏自带的同名程序集）：

### Lidgren.Network —— `PGvZOnlineMod/Lidgren.Network.Core.dll`

- 上游：<https://github.com/lidgren/lidgren-network-gen3>
- 许可：MIT License
- 版权：Copyright © 2012 Michael Lidgren
  （取自该 DLL 自带的文件属性 `LegalCopyright = "Copyright © 2012"`，版本号 2012.1.7.0）
- 在本模组里的用途：UDP 传输层——可靠有序通道、不可靠快照通道、连接与槽位管理

### Newtonsoft.Json —— `PGvZOnlineMod/Newtonsoft.Json.dll`

- 上游：<https://www.newtonsoft.com/json>
- 许可：MIT License
- 版权：Copyright © James Newton-King 2008（取自该 DLL 自带的 `LegalCopyright` 字段）
- 在本模组里的用途：`联机配置.json` 的读写

两者的上游许可都是 MIT 类型（已核对上游仓库的许可证标识），版权行各自写着本人的作者名；
需要引用具体条文时请直接看上游仓库的 `LICENSE`，本文件只登记"分发时要带声明"这件事。

## 二、只引用、不分发的程序集

以下程序集由游戏运行时自己提供，csproj 用 `HintPath` + `Private=false` 引用，
**本仓库和发布包都不复制它们**。只有当你把整个游戏目录再分发时，才需要按各自上游的要求附带声明——
它们的许可条款请以各自上游仓库为准，本文件不做转述：

- `Lawn.dll`、`LawnCommon` 等 —— 《植物大战僵尸》/《植物娘大战僵尸 HD》本体程序集，
  版权归各自权利方，本模组不授予任何权利
- `MonoGame.Framework.dll` —— MonoGame 团队
- `IronPython.dll`、`Microsoft.Scripting.dll` —— IronPython 贡献者
- `MonoMod.RuntimeDetour.dll`、`MonoMod.Utils.dll`、`Mono.Cecil.dll` —— MonoMod 相关作者

## 三、本仓库不含游戏资源

本仓库只有本模组的源码与文档：**不含**游戏可执行文件、DLL、美术/音频资源、存档或任何
《植物大战僵尸》/《植物娘大战僵尸 HD》的受版权保护内容。MIT 许可只覆盖本仓库自有源码，
**不授予对上述游戏本体及其资源任何权利**；使用本模组需自行遵守游戏本体的条款。
