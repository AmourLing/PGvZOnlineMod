# -*- coding: utf-8 -*-
"""
中继服务端本机回环自测（不需要云、不需要游戏）。

它同时扮演四个角色，全部走 127.0.0.1：
  · 中继进程        —— 直接跑编出来的 PGvZRelay.dll
  · 假游戏进程      —— 绑在随机本地端口的 UDP 服务，收到什么就回 "ECHO:"+原样
  · 主机侧三条隧道  —— 模组将来要干的事：各自登记一个槽位，在中继与假游戏之间搬运
  · 客人            —— 各发一个内容不同的包，检查回包

要验的事：
  1) 假游戏看到三个互不相同的源端口（否则主机把三个客人认成一个，整套设计不成立）
  2) 每个客人拿回的是自己那个包（没串线）
  3) 回包的源端口 == 客人当初拨的那个口（否则客人的 Lidgren 会认为对端变了）
  4) 控制协议：HOST/SEEN/JOIN/LIST 的字段对得上，房间列表能反映关卡与人数
  5) 建房密码：错密码被拒、对密码放行；LIST 里标出"要密码"
  6) 房间空闲后被回收，回收出来的端口段能被下一个房间重新绑上

端口一律每次运行现挑（空闲端口 + 连续端口段），这样上一轮没清干净的僵尸中继
不可能被这一轮误当成被测对象——真出过一次：旧实例占着 27270，新实例控制线程炸了，
测试却对着旧进程一路绿灯。
"""
import os
import random
import socket
import subprocess
import sys
import threading
import time

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net6.0", "PGvZRelay.dll")
if not os.path.exists(DLL):
    DLL = os.path.join(HERE, "bin", "Debug", "net6.0", "PGvZRelay.dll")

MAX_ROOMS = 5
IDLE = 4
HOST = "127.0.0.1"

fails = []
relay_log = []
seen_sources = {}
game_ready = threading.Event()


class RelayDead(Exception):
    pass


def check(name, ok, detail=""):
    print(("  [通过] " if ok else "  [失败] ") + name + ("  —— " + detail if detail else ""))
    if not ok:
        fails.append(name)


def free_port():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind((HOST, 0))
    p = s.getsockname()[1]
    s.close()
    return p


def free_block(n):
    """找一段连续 n 个都可用的 UDP 端口，占住再放掉，交给中继自己绑。"""
    for _ in range(300):
        base = random.randint(40000, 60000)
        socks = []
        try:
            for k in range(n):
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                s.bind((HOST, base + k))
                socks.append(s)
        except OSError:
            for s in socks:
                s.close()
            continue
        for s in socks:
            s.close()
        return base
    raise RuntimeError("找不到连续空闲端口段")


def talk(sock, msg):
    sock.sendto(msg.encode("utf-8"), (HOST, CONTROL))
    sock.settimeout(5)
    return sock.recvfrom(512)[0].decode("utf-8", "replace").strip()


def send(sock, msg):
    """只发不收：SEEN 这类报文服务端故意不应答"""
    sock.sendto(msg.encode("utf-8"), (HOST, CONTROL))


def parse_rooms(reply):
    """ROOMS|数量|code,name,level,players,max,locked|..."""
    f = reply.split("|")
    if len(f) < 2 or f[0] != "ROOMS":
        return None
    out = []
    for rec in f[2:]:
        c = rec.split(",")
        if len(c) < 6:
            continue
        out.append({"code": c[0], "name": c[1], "level": int(c[2]),
                    "players": int(c[3]), "max": int(c[4]), "locked": c[5] == "1"})
    return {"count": int(f[1]), "rooms": out}


def fake_game(sock):
    sock.settimeout(6)
    try:
        while True:
            data, frm = sock.recvfrom(2048)
            seen_sources.setdefault(frm[1], data)
            sock.sendto(b"ECHO:" + data, frm)
    except socket.timeout:
        pass
    except OSError:
        pass
    finally:
        game_ready.set()


def tunnel(sock, code, slot, host_port, stop):
    sock.sendto(("PGVZREG %s %d" % (code, slot)).encode(), (HOST, host_port))
    sock.settimeout(0.5)
    while not stop.is_set():
        try:
            data, frm = sock.recvfrom(2048)
        except socket.timeout:
            continue
        except OSError:
            return
        if frm[1] == host_port:
            sock.sendto(data, (HOST, GAME_PORT))     # 中继 → 游戏
        else:
            sock.sendto(data, (HOST, host_port))     # 游戏 → 中继


def guard(proc):
    """中继一旦死了或日志里出现致命异常就立刻中止：后面所有断言都会变成误导。"""
    rc = proc.poll()
    if rc is not None:
        raise RelayDead("进程已退出 returncode=%s" % rc)
    for line in relay_log:
        if "Unhandled exception" in line or "绑定失败" in line or "10048" in line:
            raise RelayDead(line)


def run(proc, ctl):
    guard(proc)

    # ---------- 建房 ----------
    r = talk(ctl, "HOST|测试主机|")
    f = r.split("|")
    ok = len(f) == 4 and f[0] == "ROOM"
    check("HOST 建房返回 ROOM|码|guest口|host口", ok, r)
    if not ok:
        return
    code, guest_port, host_port = f[1], int(f[2]), int(f[3])
    check("端口取自本次运行的端口段", BASE <= guest_port < BASE + MAX_ROOMS * 2
          and guest_port == host_port + 1, "guest=%d host=%d base=%d" % (guest_port, host_port, BASE))

    # ---------- 假游戏 + 三条隧道 ----------
    game = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    game.bind((HOST, GAME_PORT))
    threading.Thread(target=fake_game, args=(game,), daemon=True).start()

    stops, tunnels = [], []
    for slot in range(3):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.bind((HOST, 0))
        st = threading.Event()
        threading.Thread(target=tunnel, args=(s, code, slot, host_port, st), daemon=True).start()
        stops.append(st)
        tunnels.append(s)
    time.sleep(0.6)

    # ---------- 三个客人 ----------
    guests = []
    for i in range(3):
        g = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        g.bind((HOST, 0))
        j = talk(ctl, "JOIN|%s|" % code)
        if not j.startswith("OK|"):
            check("JOIN 客人%d" % (i + 1), False, j)
            guests.append((g, None, None))
            break
        gp = int(j.split("|")[1])
        payload = ("hello-from-guest-%d" % (i + 1)).encode()
        g.sendto(payload, (HOST, gp))
        g.settimeout(5)
        try:
            data, frm = g.recvfrom(2048)
        except socket.timeout:
            check("客人%d 收到回包" % (i + 1), False, "超时")
            guests.append((g, None, None))
            continue
        guests.append((g, data, frm))
        check("客人%d 收到自己那份回包" % (i + 1), data == b"ECHO:" + payload, repr(data[:40]))
        check("客人%d 回包来自它拨的那个口" % (i + 1), frm == (HOST, gp), str(frm))
        guard(proc)

    ports = sorted(seen_sources.keys())
    check("假游戏看到 3 个互不相同的源端口（主机能区分客人）", len(ports) == 3, str(ports))
    check("三个客人的包没有串到同一个槽位",
          len({v for v in seen_sources.values()}) == 3, str(sorted(seen_sources.values())))

    # ---------- SEEN / LIST ----------
    send(ctl, "SEEN|%s|7|2|4|" % code)
    time.sleep(0.2)
    lst = parse_rooms(talk(ctl, "LIST"))
    room_a = next((x for x in lst["rooms"] if x["code"] == code), None) if lst else None
    check("SEEN 之后 LIST 反映关卡与人数",
          room_a is not None and room_a["level"] == 7 and room_a["players"] == 2
          and room_a["max"] == 4 and room_a["name"] == "测试主机" and not room_a["locked"],
          str(room_a))

    # ---------- 建房密码 ----------
    r2 = talk(ctl, "HOST|加密房|abc123")
    f2 = r2.split("|")
    check("带密码建房成功", len(f2) == 4 and f2[0] == "ROOM", r2)
    code2 = f2[1] if len(f2) == 4 else ""
    lst2 = parse_rooms(talk(ctl, "LIST"))
    room_b = next((x for x in lst2["rooms"] if x["code"] == code2), None) if lst2 else None
    check("LIST 标出该房要密码", room_b is not None and room_b["locked"], str(room_b))
    check("错密码被拒", talk(ctl, "JOIN|%s|wrong" % code2) == "ERR|passwd")
    check("对密码放行", talk(ctl, "JOIN|%s|abc123" % code2).startswith("OK|"))
    check("不存在的房间返回 noready", talk(ctl, "JOIN|000000|") == "ERR|noready")
    guard(proc)

    for g, _, _ in guests:
        g.close()
    for st in stops:
        st.set()
    for s in tunnels:
        s.close()
    game.close()
    time.sleep(0.3)

    # ---------- 空闲回收 + 端口段复用 ----------
    deadline = time.time() + IDLE + 16.0
    lst3 = None
    while time.time() < deadline:
        lst3 = parse_rooms(talk(ctl, "LIST"))
        if lst3 and lst3["count"] == 0:
            break
        time.sleep(1.0)
    check("空闲 %ds 后两个房间都被回收" % IDLE, lst3 is not None and lst3["count"] == 0, str(lst3))

    # 回收的槽位号不保证按顺序回来（FreeIndex 是无序集合），所以只验"新房间能在同一段里
    # 把两个端口重新绑上"——绑不上就返回 ERR|full，这才是复用能力真正的证据。
    r3 = talk(ctl, "HOST|第二个主机|")
    f3 = r3.split("|")
    check("回收后端口段能被新房间重新占用",
          len(f3) == 4 and f3[0] == "ROOM" and BASE <= int(f3[2]) < BASE + MAX_ROOMS * 2
          and int(f3[2]) == int(f3[3]) + 1, r3)
    check("新房间只有自己一个", (parse_rooms(talk(ctl, "LIST")) or {}).get("count") == 1)


def main():
    global CONTROL, BASE, GAME_PORT
    if not os.path.exists(DLL):
        print("找不到 PGvZRelay.dll，先 dotnet build RelayServer -c Release")
        return 2

    CONTROL = free_port()
    BASE = free_block(MAX_ROOMS * 2)
    GAME_PORT = free_port()
    print("本次端口：控制=%d 端口段=%d~%d 假游戏=%d" % (CONTROL, BASE, BASE + MAX_ROOMS * 2 - 1, GAME_PORT))

    proc = subprocess.Popen(
        ["dotnet", DLL, "--control", str(CONTROL), "--base", str(BASE),
         "--rooms", str(MAX_ROOMS), "--idle", str(IDLE)],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        encoding="utf-8", errors="replace")
    threading.Thread(target=lambda: [relay_log.append(l.rstrip()) for l in proc.stdout],
                     daemon=True).start()

    ctl = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        for _ in range(40):
            try:
                if talk(ctl, "PING") == "PONG":
                    break
            except Exception:
                if proc.poll() is not None:
                    break
            time.sleep(0.25)
        else:
            check("中继启动", False, "PING 无响应")
            return 1
        check("中继启动", True, "控制口 %d" % CONTROL)

        run(proc, ctl)
    except RelayDead as e:
        check("中继全程存活", False, str(e))
    finally:
        proc.terminate()
        try:
            proc.wait(5)
        except Exception:
            proc.kill()
        ctl.close()

    print("--- 中继日志（尾部 %d 行）---" % (14 if not fails else len(relay_log)))
    for line in (relay_log[-14:] if not fails else relay_log):
        print("   " + line)
    print("== 自测结果：%s ==" % ("全部通过" if not fails else "失败 %d 项：%s" % (len(fails), "、".join(fails))))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
