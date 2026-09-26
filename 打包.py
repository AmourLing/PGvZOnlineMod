# -*- coding: utf-8 -*-
"""
PGvZOnlineMod 打包脚本：
  1. dotnet Release 编译 mod（含 NuGet 损坏时的降级链）
  2. 编译并运行 VerifyHost 离线回归（失败即中止打包）
  3. 产出 publish/PGvZOnlineMod_CSharp_发布_时间戳.zip（mod DLL + 使用说明）

用法:  python 打包.py
"""
import os
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
PUBLISH = os.path.join(ROOT, "publish")
MOD_DLL = os.path.join(ROOT, "bin", "Release", "net6.0", "PGvZOnlineMod.dll")
README = os.path.join(ROOT, "使用说明.txt")
TEMPLATE = os.path.join(ROOT, "使用说明模板.txt")
CONFIG_SRC = os.path.join(ROOT, "配置模板", "联机配置.json")
NOTICES = os.path.join(ROOT, "THIRD-PARTY-NOTICES.md")
GAME_DLL_DIR = os.path.join(ROOT, "..", "..", "PlantGirlsVsZombies", "LawnDLL")
LIDGRE_DLL = os.path.join(GAME_DLL_DIR, "Lidgren.Network.Core.dll")
NEWTONSOFT_DLL = os.path.join(GAME_DLL_DIR, "Newtonsoft.Json.dll")


def run(cmd, cwd=None, capture=True):
    print("+ " + " ".join(cmd))
    r = subprocess.run(
        cmd, cwd=cwd or ROOT,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
        text=True, encoding="utf-8", errors="replace",
    )
    if capture and r.stdout:
        print(r.stdout.rstrip()[-3000:])
    return r.returncode


def build_mod():
    """正常还原失败（本机 NuGet 组件损坏的历史问题）时走降级链，与 AI对话 mod 相同。"""
    rc = run(["dotnet", "build", "-c", "Release"])
    if rc == 0:
        return True
    print("!! 常规编译失败，尝试降级链（跳过 NuGet 目标）")
    rc = run([
        "dotnet", "build", "-c", "Release", "--no-restore",
        "-p:SkipResolvePackageAssets=true",
        "-p:GenerateDependencyFile=false",
        "-p:GenerateRuntimeConfigurationFiles=false",
    ])
    return rc == 0 and os.path.exists(MOD_DLL)


def main():
    os.makedirs(PUBLISH, exist_ok=True)

    if not build_mod():
        print("## mod 编译失败，中止")
        return 1
    print("## mod 编译 OK")

    print("## VerifyHost 回归...")
    rc = run(["dotnet", "build", "VerifyHost", "-c", "Release"])
    if rc != 0:
        print("## VerifyHost 编译失败，中止")
        return 1
    rc = run(["dotnet", "run", "--project", "VerifyHost", "-c", "Release", "--no-build"],
             capture=False)
    if rc != 0:
        print("## VerifyHost 回归未全过（exit=%d），中止" % rc)
        return 1
    print("## VerifyHost 回归 OK")

    # 包内说明一律从模板重新生成：仓库里的 使用说明.txt 只是上次打包的产物，
    # 沿用会把过期文案打进发布包（历史上漏出过"双人共用卡槽"的旧版）。
    if not os.path.exists(TEMPLATE):
        print("## 缺少 %s，中止" % TEMPLATE)
        return 1
    shutil.copyfile(TEMPLATE, README)
    print("## 使用说明已从模板重新生成")

    if not os.path.exists(CONFIG_SRC):
        print("## 缺少 %s，中止" % CONFIG_SRC)
        return 1
    # 再分发第三方 DLL 就必须把它们的声明一起发出去：缺这份宁可不打包，也别发不合规的包
    if not os.path.exists(NOTICES):
        print("## 缺少 %s，中止（发布包要带第三方许可声明）" % NOTICES)
        return 1
    for dep, name in ((LIDGRE_DLL, "Lidgren.Network.Core.dll"), (NEWTONSOFT_DLL, "Newtonsoft.Json.dll")):
        if not os.path.exists(dep):
            print("## 缺少依赖 %s，中止" % dep)
            return 1

    stamp = time.strftime("%Y%m%d_%H%M%S")
    zip_path = os.path.join(PUBLISH, "PGvZOnlineMod_CSharp_发布_%s.zip" % stamp)
    # zip 内不带 mods/ 包装层：把 zip 里所有内容解压/复制进 mods\ 即完成安装。
    # 依赖 DLL 放数据目录 PGvZOnlineMod/：Windows 用游戏自带的同一份（按程序集标识复用），
    # Android 版游戏不带 Lidgren/Newtonsoft，模组启动时从该目录自动加载。
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(MOD_DLL, "PGvZOnlineMod.dll")
        z.write(CONFIG_SRC, "PGvZOnlineMod/联机配置.json")
        z.write(LIDGRE_DLL, "PGvZOnlineMod/Lidgren.Network.Core.dll")
        z.write(NEWTONSOFT_DLL, "PGvZOnlineMod/Newtonsoft.Json.dll")
        # 声明挨着那两个 DLL 放（它们才是这份声明描述的对象），mods\ 顶层不留杂物
        z.write(NOTICES, "PGvZOnlineMod/THIRD-PARTY-NOTICES.md")
        z.write(README, "使用说明.txt")
    print("## 打包完成: %s (%d bytes)" % (zip_path, os.path.getsize(zip_path)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
