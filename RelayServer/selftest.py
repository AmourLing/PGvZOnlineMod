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
  7) 公网那三道闸门：不带控制协议版本的 HOST 被拒、游戏协议双向不匹配被拒、
     没有令牌的 LIST 被拒且应答更小、密码连错越过上限转 slow
  8) PONG 带回来的房数与容量同 LIST 的条数一致（联机页左列那行靠的就是这个一致性）

v2 的两处形状：版本号一律追加在字段尾部；LIST 要带 PONG 发下来的令牌。
控制协议版本与游戏协议版本都从 src/ 里的源文件读出来，不在这份脚本里另抄一遍——
抄的那份会随升号漂，漂了还一路绿灯是最坏的结果。

端口一律每次运行现挑（空闲端口 + 连续端口段），这样上一轮没清干净的僵尸中继
不可能被这一轮误当成被测对象——真出过一次：旧实例占着 27270，新实例控制线程炸了，
测试却对着旧进程一路绿灯。

打公网那台中继：`py -3 RelayServer/selftest.py --remote 47.116.78.238`
不启本地中继，假游戏与三条隧道仍在 127.0.0.1，验的是"客人出网到服务器、
服务器再回落进主机侧隧道"这条真实路径。空闲回收要等服务器自己的 60 秒，
公网模式下跳过那一段（本地模式照样跑）。
"""
import os
import random
import re
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

SRC = os.path.join(os.path.dirname(HERE), "src", "Protocol")


def source_const(path, name):
    """把权威源文件里的 const int 读出来；读不到返回 None，由调用方报失败，绝不退回猜值。"""
    try:
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
    except OSError:
        return None
    m = re.search(r"const\s+int\s+" + name + r"\s*=\s*(\d+)", text)
    return m.group(1) if m else None


# 版本号只认这一个来源：脚本里再抄一份数字，升号之后这份自测会绿得毫无意义
CTL = source_const(os.path.join(SRC, "RelayProtocol.cs"), "Version")
GAME = source_const(os.path.join(SRC, "Packets.cs"), "Current")
PW_LIMIT = source_const(os.path.join(SRC, "RelayProtocol.cs"), "MaxPasswdFails")

PING_LINE = "PING|" + (CTL or "")


def host_line(name, pwd, game=None):
    return "HOST|%s|%s|%s|%s" % (name, pwd, CTL, GAME if game is None else game)


def join_line(code, pwd, game=None):
    return "JOIN|%s|%s|%s|%s" % (code, pwd, CTL, GAME if game is None else game)


def list_line(token=""):
    return "LIST|" + token


def get_token(ctl, port=None):
    """令牌由 PONG 发下来：拿不到就返回空串，后面的 LIST 会当场失败而不是悄悄跳过。"""
    f = talk(ctl, PING_LINE, port).split("|")
    return f[2] if len(f) >= 5 and f[0] == "PONG" else ""


def rooms(ctl, port=None):
    """先探活拿令牌再要房间表——这两步现在是绑在一起的。"""
    return parse_rooms(talk(ctl, list_line(get_token(ctl, port)), port))

MAX_ROOMS = 5
IDLE = 4
LOCAL = "127.0.0.1"          # 假游戏与自己这些 socket 永远在本地
PUBLIC_RANGE = (27200, 27229)  # 服务器上那台中继的端口段（--rooms 15 --base 27200）

# --remote <ip>：打公网那台中继，不启本地进程
REMOTE = sys.argv[sys.argv.index("--remote") + 1] if "--remote" in sys.argv else None
HOST = REMOTE or LOCAL
CONTROL = 27270 if REMOTE else None
# 要往中继发包的 socket（客人、主机侧隧道）在公网模式下必须绑通配地址：
# 绑在 127.0.0.1 上的 socket 发不出外网包，Windows 直接给 WSAENETUNREACH(10051)。
# 假游戏仍绑 127.0.0.1——它就该只在本机。
BIND_ANY = LOCAL if REMOTE is None else ""

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
    s.bind((LOCAL, 0))
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
                s.bind((LOCAL, base + k))
                socks.append(s)
        except OSError:
            for s in socks:
                s.close()
            continue
        for s in socks:
            s.close()
        return base
    raise RuntimeError("找不到连续空闲端口段")


def talk(sock, msg, port=None):
    sock.sendto(msg.encode("utf-8"), (HOST, port or CONTROL))
    sock.settimeout(5)
    return sock.recvfrom(512)[0].decode("utf-8", "replace").strip()


def send(sock, msg, port=None):
    """只发不收：SEEN 这类报文服务端故意不应答"""
    sock.sendto(msg.encode("utf-8"), (HOST, port or CONTROL))


def spawn(control, base, idle, stat, loglist, extra=()):
    """起一台中继并把 stdout 抽进 loglist。
       台账那段要的是"这些行确实落出来了"，所以用专用实例：--idle 给足 60 秒，
       免得用例跑到一半房被回收；--stat 5 让心跳一行在十几秒内出现。"""
    proc = subprocess.Popen(
        ["dotnet", DLL, "--control", str(control), "--base", str(base),
         "--rooms", str(MAX_ROOMS), "--idle", str(idle), "--stat", str(stat)] + list(extra),
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        encoding="utf-8", errors="replace")
    threading.Thread(target=lambda: [loglist.append(l.rstrip()) for l in proc.stdout],
                     daemon=True).start()
    return proc


def wait_ready(control, secs=20.0):
    deadline = time.time() + secs
    while time.time() < deadline:
        try:
            if talk(socket.socket(socket.AF_INET, socket.SOCK_DGRAM), PING_LINE, control)\
                    .split("|")[0] == "PONG":
                return True
        except Exception:
            pass
        time.sleep(0.25)
    return False


def wait_log(lines, pattern, secs=25.0):
    """等一条能匹配 pattern 的日志行：日志是另一条线程落出来的，发完报文就去读会读到空的。"""
    rx = re.compile(pattern)
    deadline = time.time() + secs
    while time.time() < deadline:
        for l in list(lines):
            if rx.search(l):
                return l
        time.sleep(0.2)
    return None


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
            sock.sendto(data, (LOCAL, GAME_PORT))    # 中继 → 游戏（游戏永远在本地）
        else:
            sock.sendto(data, (HOST, host_port))     # 游戏 → 中继（公网模式下这条要穿 NAT）


def guard(proc):
    """中继一旦死了或日志里出现致命异常就立刻中止：后面所有断言都会变成误导。
       公网模式没有本地进程（proc is None），只查日志里带回来的错误。"""
    if proc is None:
        return
    rc = proc.poll()
    if rc is not None:
        raise RelayDead("进程已退出 returncode=%s" % rc)
    for line in relay_log:
        if "Unhandled exception" in line or "绑定失败" in line or "10048" in line:
            raise RelayDead(line)


def run(proc, ctl):
    guard(proc)

    # ---------- 建房 ----------
    r = talk(ctl, host_line("测试主机", ""))
    f = r.split("|")
    ok = len(f) == 4 and f[0] == "ROOM"
    check("HOST 建房返回 ROOM|码|guest口|host口", ok, r)
    if not ok:
        return
    code, guest_port, host_port = f[1], int(f[2]), int(f[3])
    lo, hi = (PUBLIC_RANGE if REMOTE else (BASE, BASE + MAX_ROOMS * 2 - 1))
    check("端口取自服务器自己的端口段 %d~%d" % (lo, hi),
          lo <= guest_port <= hi and guest_port == host_port + 1,
          "guest=%d host=%d" % (guest_port, host_port))

    # ---------- 假游戏 + 三条隧道 ----------
    game = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    game.bind((LOCAL, GAME_PORT))
    threading.Thread(target=fake_game, args=(game,), daemon=True).start()

    stops, tunnels = [], []
    for slot in range(3):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.bind((BIND_ANY, 0))
        st = threading.Event()
        threading.Thread(target=tunnel, args=(s, code, slot, host_port, st), daemon=True).start()
        stops.append(st)
        tunnels.append(s)
    time.sleep(0.6)

    # ---------- 三个客人 ----------
    guests = []
    for i in range(3):
        g = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        g.bind((BIND_ANY, 0))
        j = talk(ctl, join_line(code, ""))
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
    lst = rooms(ctl)
    room_a = next((x for x in lst["rooms"] if x["code"] == code), None) if lst else None
    check("SEEN 之后 LIST 反映关卡与人数",
          room_a is not None and room_a["level"] == 7 and room_a["players"] == 2
          and room_a["max"] == 4 and room_a["name"] == "测试主机" and not room_a["locked"],
          str(room_a))

    # ---------- 建房密码 ----------
    r2 = talk(ctl, host_line("加密房", "abc123"))
    f2 = r2.split("|")
    check("带密码建房成功", len(f2) == 4 and f2[0] == "ROOM", r2)
    code2 = f2[1] if len(f2) == 4 else ""
    lst2 = rooms(ctl)
    room_b = next((x for x in lst2["rooms"] if x["code"] == code2), None) if lst2 else None
    check("LIST 标出该房要密码", room_b is not None and room_b["locked"], str(room_b))
    check("错密码被拒", talk(ctl, join_line(code2, "wrong")) == "ERR|passwd")
    check("对密码放行", talk(ctl, join_line(code2, "abc123")).startswith("OK|"))
    check("不存在的房间返回 noready", talk(ctl, join_line("000000", "")) == "ERR|noready")
    guard(proc)

    for g, _, _ in guests:
        g.close()
    for st in stops:
        st.set()
    for s in tunnels:
        s.close()
    game.close()
    time.sleep(0.3)

    # ---------- 空闲回收 + 端口段复用（本地才跑：服务器自己的 idle 是 60 秒） ----------
    if REMOTE:
        # 公网模式不等回收，改验"主动 DROP 立刻放掉房间"——主机离开房间走的就是这条，
        # 不生效的后果是别人 LIST 里全是不存在的空房
        before = rooms(ctl)
        talk(ctl, "DROP|%s" % code)
        time.sleep(0.3)
        after = rooms(ctl)
        check("公网：主动 DROP 立刻把房间放掉",
              before and after and after["count"] < before["count"],
              "%s → %s" % (before and before["count"], after and after["count"]))
        return
    deadline = time.time() + IDLE + 16.0
    lst3 = None
    while time.time() < deadline:
        lst3 = rooms(ctl)
        if lst3 and lst3["count"] == 0:
            break
        time.sleep(1.0)
    check("空闲 %ds 后两个房间都被回收" % IDLE, lst3 is not None and lst3["count"] == 0, str(lst3))

    # 回收的槽位号不保证按顺序回来（FreeIndex 是无序集合），所以只验"新房间能在同一段里
    # 把两个端口重新绑上"——绑不上就返回 ERR|full，这才是复用能力真正的证据。
    r3 = talk(ctl, host_line("第二个主机", ""))
    f3 = r3.split("|")
    check("回收后端口段能被新房间重新占用",
          len(f3) == 4 and f3[0] == "ROOM" and BASE <= int(f3[2]) < BASE + MAX_ROOMS * 2
          and int(f3[2]) == int(f3[3]) + 1, r3)
    check("新房间只有自己一个", (rooms(ctl) or {}).get("count") == 1)


def gates(proc, ctl):
    """公网那三道只能服务端守的门。

    放在最后跑：密码限速是**按来源地址**计数的，本机这些 socket 的地址全是同一个，
    这段先跑的话上面那些正常用例会被自己的限速挡掉。
    """
    print("--- 公网闸门 ---")
    guard(proc)

    r = talk(ctl, "HOST|旧模组|pw")
    check("不带控制协议版本的 HOST 被拒（旧模组不会静默建出一个没人管得住的房）",
          r == "ERR|oldctl", r)
    # 用"LIST 里有没有这个房名"当证据，而不是比房间总数：本地那台的空闲回收只有 4 秒，
    # 比总数会撞上别的房间正好在这中间被回收，红一条假失败
    names = [x["name"] for x in (rooms(ctl) or {}).get("rooms", [])]
    check("被拒的 HOST 没建出房来（列表里找不到它）", "旧模组" not in names, str(names))

    f = talk(ctl, host_line("闸门房", "gate123")).split("|")
    ok = len(f) == 4 and f[0] == "ROOM"
    check("带版本号的 HOST 建房成功", ok, str(f))
    if not ok:
        return
    gcode = f[1]

    lo = talk(ctl, join_line(gcode, "gate123", str(int(GAME) - 1)))
    check("客人比房主旧 → ERR|old，并把房主的版本带回来", lo == "ERR|old|%s" % GAME, lo)
    hi = talk(ctl, join_line(gcode, "gate123", str(int(GAME) + 1)))
    check("客人比房主新 → ERR|new（双向都拦，只拦一边等于换个方向继续踩）",
          hi == "ERR|new|%s" % GAME, hi)
    check("版本相同放行", talk(ctl, join_line(gcode, "gate123")).startswith("OK|"), "")

    naked = talk(ctl, "LIST")
    check("不带令牌的 LIST 被拒", naked == "ERR|token", naked)
    size = len(naked.encode("utf-8"))
    check("那一路的应答不超过 16 字节（满房时 ROOMS 是 336 字节，这就是放大的那一面）",
          size <= 16, "%d 字节：%s" % (size, naked))
    tok = get_token(ctl)
    check("令牌确实由 PONG 发下来（不是脚本自己编的）", len(tok) >= 6, "长度=%d" % len(tok))

    other = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    other.bind((BIND_ANY, 0))          # 另一个来源端点：令牌是按端点发的
    stolen = talk(other, list_line(tok))
    check("拿别的来源的令牌冒充 → 同样被拒", stolen == "ERR|token", stolen)
    other.close()

    got = parse_rooms(talk(ctl, list_line(tok)))
    names = [x["name"] for x in got["rooms"]] if got else []
    check("持自己那枚令牌 LIST 得到房间表", got is not None and gcode in [x["code"] for x in got["rooms"]],
          str(got))
    check("尾部那两个版本号没串进房名", "闸门房" in names, str(names))

    pong = talk(ctl, PING_LINE).split("|")
    listed = parse_rooms(talk(ctl, list_line(pong[2]))) if len(pong) >= 5 else None
    check("PONG 里的房数与 LIST 的条数一致（左列那行 "
          "3/15 才不是编的）",
          len(pong) >= 5 and listed is not None and int(pong[3]) == listed["count"] and int(pong[4]) > 0,
          "pong=%s list=%s" % (pong[3:5] if len(pong) >= 5 else pong, listed and listed["count"]))

    last = ""
    for i in range(int(PW_LIMIT)):
        last = talk(ctl, join_line(gcode, "猜的密码%d" % i))
    check("上限之内错密码照常回 passwd（正常手滑不该被限速挡住）", last == "ERR|passwd", last)
    over = talk(ctl, join_line(gcode, "再猜一次"))
    check("越过上限转成 slow：不再给猜密码的人回可读的错误", over == "ERR|slow", over)
    right = talk(ctl, join_line(gcode, "gate123"))
    check("限速期间端对密码仍然放行——拦的是猜，不是手滑的人", right.startswith("OK|"), right)

    talk(ctl, "DROP|%s" % gcode)
    guard(proc)


def ledgers():
    """服务端日志台账：现场查"开了几把、有没有人真进来、卡在哪一道门"全靠这几行，
       所以它和协议报文一样要有断言。公网模式读不到服务端 stdout，这段只在本机跑。"""
    print("--- 日志台账 ---")
    control = free_port()
    base = free_block(MAX_ROOMS * 2)
    log2 = []
    proc2 = spawn(control, base, 60, 5, log2)
    socks = []
    try:
        check("台账实例启动", wait_ready(control), "控制口 %d" % control)
        c = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)      # 房主的控制口
        socks.append(c)
        r = talk(c, host_line("台账房", ""), control)
        f = r.split("|")
        if len(f) != 4 or f[0] != "ROOM":
            check("建房", False, r)
            return
        code, gp, hp = f[1], int(f[2]), int(f[3])

        # 一条隧道登记 + 两个客人占槽：放行/峰值客人/隧道三个数各不相同，
        # 谁被写死成一个常数都会在下面那条台账行上露馅
        tk = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        tk.bind((BIND_ANY, 0))
        socks.append(tk)
        st = threading.Event()
        threading.Thread(target=tunnel, args=(tk, code, 0, hp, st), daemon=True).start()
        j = talk(c, join_line(code, ""), control)
        if not j.startswith("OK|"):
            check("客人 JOIN", False, j)
            return
        line = wait_log(log2, r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} 建房 " + code +
                        r" 主机=台账房 房主=127\.0\.0\.1:\d+ 协议=v\d+ 密码=无 "
                        r"guest=\d+ host=\d+（当前 \d+/\d+ 房）")
        check("建房一行带全日期、房主端点、协议与容量", line is not None,
              "没等到这样一行" if line is None else line)
        check("放行客人一行带来源与第几次",
              wait_log(log2, code + r" 放行客人 127\.0\.0\.1:\d+ 协议=v\d+ 第 1 次放行（房=台账房 隧道 \d/3）") is not None)

        guests = []
        for i in range(2):
            g = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            g.bind((BIND_ANY, 0))
            g.sendto(b"ledger-guest-%d" % i, (HOST, gp))
            guests.append(g)
            socks.append(g)
        time.sleep(0.6)

        # 非房主想关别人的房：端口不同的本地 socket 就是另一个端点
        other = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        other.bind((BIND_ANY, 0))
        socks.append(other)
        oport = other.getsockname()[1]
        check("非房主的 DROP 被拒", talk(other, "DROP|%s" % code, control) == "ERR|noperm")
        check("被拒之后房还在", (rooms(c, control) or {}).get("count") == 1)
        check("非房主 DROP 留下带端点的一行",
              wait_log(log2, r"DROP 来自非房主 127\.0\.0\.1:" + str(oport)) is not None)

        # 客人侧那些"故意不应答"的路径也要留痕，否则现场查不到
        send(c, "SEEN|999999|1|1|1|", control)
        check("SEEN 指向不存在的房会留一行",
              wait_log(log2, r"SEEN 指向不存在的房 999999（房主 127\.0\.0\.1:\d+，多半已被回收）") is not None)
        check("JOIN 不存在的房号返回 noready", talk(c, join_line("000000", "", control), control) == "ERR|noready")
        check("JOIN 不存在的房号会留一行",
              wait_log(log2, r"JOIN 房号 000000 不存在，拒绝 127\.0\.0\.1:\d+") is not None)
        c.sendto("GARBAGE\x01\x02\x03\r\n伪造一行".encode("utf-8"), (HOST, control))
        # 期望里带着"CR/LF 与不可见字符已经被剥掉"：日志行能不能被外来报文伪造，看的就是这一条
        check("无法解析的控制报文只记一条、不回话、且被洗干净后才进日志",
              wait_log(log2, r"无法解析的报文（127\.0\.0\.1:\d+）: \"GARBAGE伪造一行\"") is not None)

        st.set()
        talk(c, "DROP|%s" % code, control)
        line = wait_log(log2, r"回收 " + code +
                        r" 台账: 主机=台账房 房主=127\.0\.0\.1:\d+ 协议=v\d+ 存活=\S+ "
                        r"放行=1 峰值客人=2/3 隧道=1/3 原因=主机主动关闭 剩余=\d+/\d+房")
        check("回收一行是一整条台账（放行/峰值客人/隧道三个数各归各）", line is not None,
              "没等到这样一行" if line is None else line)

        # 心跳行必须真在累加，而不是把三个数写死在格式串里
        beat = wait_log(log2, r"心跳 运行=\S+ · 在房=\d+/\d+ · 今日\(\d{2}-\d{2}\) "
                             r"建房=1 放行=1 客人占槽=2 隧道登记=1[ ].*累计 ")
        check("心跳一行报出运行时长与各项计数", beat is not None,
              beat if beat else "没等到 5 秒节拍的心跳行，或那行里的计数对不上")

        # 第二台：带 --inject-slot-leak，验台账不会把"没人来"这种形状当成理所当然
        control2 = free_port()
        base2 = free_block(MAX_ROOMS * 2)
        log3 = []
        proc3 = spawn(control2, base2, 60, 5, log3, ["--inject-slot-leak"])
        try:
            check("注入实例启动", wait_ready(control2), "控制口 %d" % control2)
            check("注入实例在启动行里自报家门",
                  any("自测注入" in l for l in log3), str(log3[:2]))
            c2 = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            socks.append(c2)
            f2 = talk(c2, host_line("漏槽房", ""), control2).split("|")
            ok2 = len(f2) == 4 and f2[0] == "ROOM"
            check("注入实例建房成功", ok2, "拿不到房号，下面那条台账断言就没法跑")
            if ok2:
                talk(c2, "DROP|%s" % f2[1], control2)
                line = wait_log(log3, r"回收 " + f2[1] + r" 台账: .*放行=0 峰值客人=[1-9]/3 隧道=0/3")
                check("有人占槽没登记隧道时，台账照实写出非零峰值", line is not None,
                      "没等到这样一行（台账把数写死了？）" if line is None else line)
        finally:
            proc3.terminate()
            try:
                proc3.wait(5)
            except Exception:
                proc3.kill()
    finally:
        for s in socks:
            s.close()
        proc2.terminate()
        try:
            proc2.wait(5)
        except Exception:
            proc2.kill()


def main():
    global CONTROL, BASE, GAME_PORT
    proc = None
    BASE = 27200
    if REMOTE:
        CONTROL = 27270
        GAME_PORT = free_port()
        print("打到公网中继 %s:%d（不启本地进程；假游戏本地口=%d；空闲回收段跳过）"
              % (HOST, CONTROL, GAME_PORT))
    else:
        if not os.path.exists(DLL):
            print("找不到 PGvZRelay.dll，先 dotnet build RelayServer -c Release")
            return 2

        CONTROL = free_port()
        BASE = free_block(MAX_ROOMS * 2)
        GAME_PORT = free_port()
        print("本次端口：控制=%d 端口段=%d~%d 假游戏=%d" % (CONTROL, BASE, BASE + MAX_ROOMS * 2 - 1, GAME_PORT))

        proc = spawn(CONTROL, BASE, IDLE, 300, relay_log)

    ctl = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        check("自测从源文件读到了权威版本号", bool(CTL and GAME and PW_LIMIT),
              "控制协议=%s 游戏协议=%s 密码上限=%s" % (CTL, GAME, PW_LIMIT))
        if not (CTL and GAME and PW_LIMIT):
            print("读不到 src/Protocol 里的版本号，后面每条断言都不成立——直接收工")
            return 2
        for _ in range(40):
            try:
                if talk(ctl, PING_LINE).split("|")[0] == "PONG":
                    break
            except Exception:
                if proc is not None and proc.poll() is not None:
                    break
            time.sleep(0.25)
        else:
            check("中继启动", False, "PING 无响应")
            return 1
        check("中继启动", True, "控制口 %d" % CONTROL)

        run(proc, ctl)
        gates(proc, ctl)
        if not REMOTE:
            ledgers()
    except RelayDead as e:
        check("中继全程存活", False, str(e))
    finally:
        if proc is not None:
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
