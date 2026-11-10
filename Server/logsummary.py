#!/usr/bin/env python3
"""Summarises a folder of hub logs fetched with fetch-logs.ps1.

    python logsummary.py                 # the newest folder under Logs\\
    python logsummary.py <folder>

Prints, from events.jsonl and video.csv: the hub's own health over the
session, one line per game (how it ended, how the shots went, how many tanks
and phones dropped), each tank's memory and connection history over the
session, and a "worth a look" list of anything out of the ordinary.

Standard library only. The logs themselves are described in Server/README.md.
"""
import csv
import json
import os
import statistics
import subprocess
import sys
from collections import Counter, defaultdict


def newest_folder():
    here = os.path.dirname(os.path.abspath(__file__))
    top = os.path.dirname(here)
    try:  # in a Claude worktree, Logs\ is in the main checkout
        common = subprocess.run(["git", "-C", here, "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                capture_output=True, text=True).stdout.strip()
        if common:
            top = os.path.dirname(common)
    except OSError:
        pass
    logs = os.path.join(top, "Logs")
    folders = sorted(d for d in (os.path.join(logs, n) for n in os.listdir(logs)) if os.path.isdir(d)) \
        if os.path.isdir(logs) else []
    if not folders:
        sys.exit(f"No fetched logs under {logs}: run fetch-logs.ps1 first, or give a folder.")
    return folders[-1]


def read_events(folder):
    out = []
    for name in ("events.jsonl.1", "events.jsonl"):
        path = os.path.join(folder, name)
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as f:
            for line in f:
                try:
                    out.append(json.loads(line))
                except json.JSONDecodeError:
                    pass  # a line cut off where a log file filled up
    out.sort(key=lambda e: e.get("t", ""))
    return out


def read_video(folder):
    """Video log rows of kind "tank", one per tank per sample (viewers kept)."""
    rows = []
    for name in ("video.csv.1", "video.csv"):
        path = os.path.join(folder, name)
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8", newline="") as f:
            for r in csv.DictReader(f):
                if r.get("kind") == "tank":
                    rows.append(r)
    return rows


def tod(t):
    """Seconds since midnight from "2026-11-15T14:03:05.123" or "14:03:05.1"."""
    t = t[11:] if "T" in t else t
    h, m, s = t.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def num(v, default=None):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def first_last_low(values):
    return f"{values[0]:.0f} -> {values[-1]:.0f} (lowest {min(values):.0f})"


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else newest_folder()
    events = read_events(folder)
    video = read_video(folder)
    flags = []
    print(f"Logs: {folder}")
    info_path = os.path.join(folder, "info.json")
    if os.path.exists(info_path):
        info = json.load(open(info_path, encoding="utf-8"))
        print(f"Hub {info.get('version')} ({info.get('os')}/{info.get('arch')}), up {info.get('uptimeS', 0) // 60} min at fetch, hub clock {info.get('time')}")
    if not events:
        sys.exit("No events (events.jsonl is missing or empty: is the hub older than the event log?)")
    print(f"Events {events[0]['t']} to {events[-1]['t']}: {len(events)}")
    if os.path.exists(os.path.join(folder, "events.jsonl.1")):
        print("  (the event log filled up and started afresh at least once: the oldest events may be gone)")

    # ── The hub ──────────────────────────────────────────────────────────────
    starts = [e for e in events if e["ev"] == "hub_start"]
    panics = [e for e in events if e["ev"] == "panic"]
    hub = [e for e in events if e["ev"] == "hub"]
    print(f"\nHUB: started {len(starts)} time(s) in these logs; {len(panics)} panic(s)")
    if len(starts) > 1:
        flags.append(f"the hub started {len(starts)} times ({', '.join(s['t'][11:19] for s in starts)}): a deploy, or a crash?")
    for p in panics:
        flags.append(f"hub panic at {p['t'][11:19]} in {p.get('where')}: {p.get('error')} (stack in server.log)")
    if os.path.exists(os.path.join(folder, "crash-previous.txt")):
        flags.append("the hub crashed at some point: see crash-previous.txt")
    if hub:
        def col(k):
            return [h[k] for h in hub if k in h]
        for label, key, worst in (("memory used by the hub, MB", "rss_mb", max), ("router memory free, MB", "mem_avail_mb", min),
                                  ("temp folder free, MB", "tmp_free_mb", min), ("goroutines", "goroutines", max),
                                  ("Go heap, MB", "go_heap_mb", max), ("processor busy, %", "cpu_pct", max)):
            v = col(key)
            if v:
                print(f"  {label}: {v[0]:g} -> {v[-1]:g} ({'highest' if worst is max else 'lowest'} {worst(v):g})")
        g = col("goroutines")
        if len(g) > 4 and g[-1] > g[0] * 1.5 + 20:
            flags.append(f"goroutines grew from {g[0]} to {g[-1]}: something isn't being cleaned up")
        m = col("mem_avail_mb")
        if m and min(m) < 60:
            flags.append(f"router memory got down to {min(m):g} MB free")
        d = col("tmp_free_mb")
        if d and min(d) < 60:
            flags.append(f"the router's temp folder got down to {min(d):g} MB free")

    # ── Games ────────────────────────────────────────────────────────────────
    games, cur = [], None
    for e in events:
        ev = e["ev"]
        if ev == "hub_start" and cur and "end" not in cur:
            cur["cut_short"] = True  # the hub restarted mid-game
            cur = None
        if ev == "game_start":
            cur = {"start": e, "shots": [], "tank_drops": [], "tank_backs": [], "phone_drops": [], "kills": 0,
                   "playing": {t["tank"] for t in e.get("tanks", [])},
                   "crew": {n for t in e.get("tanks", []) for n in (t.get("driver"), t.get("gunner")) if n}}
            games.append(cur)
        elif cur is None or "end" in cur:
            continue
        elif ev == "play_begin":
            cur["play"] = e
        elif ev == "shot":
            cur["shots"].append(e)
        elif ev == "kill":
            cur["kills"] += 1
        elif ev == "tank_disconnect" and e.get("tank") in cur["playing"]:
            cur["tank_drops"].append(e)
        elif ev == "tank_back_in_game":
            cur["tank_backs"].append(e)
        elif ev == "phone_disconnect" and e.get("player") in cur["crew"] and e.get("pages", 0) == 0:
            cur["phone_drops"].append(e)
        elif ev == "game_end":
            cur["end"] = e

    by_time = defaultdict(dict)  # video: (tank) → list of (time of day, row), one per sample
    for r in video:
        try:
            by_time[r["tank"]][r["time"]] = r
        except KeyError:
            pass

    print(f"\nGAMES: {len(games)}")
    if games:
        print("  #  start     played  ended by     winner       shots hit miss 1brl noack stale soon | avg/slowest ms | silent | tank/phone drops")
    for i, g in enumerate(games, 1):
        s, end = g["start"], g.get("end")
        res = Counter(x.get("result") for x in g["shots"])
        real = [x for x in g["shots"] if x.get("result") not in ("too_soon", "queue_full")]
        totals = [x["total_ms"] for x in real if "total_ms" in x]
        silent = Counter(n for x in real for n in x.get("silent", []))
        offline = Counter(n for x in real for n in x.get("offline", []))
        played = f"{end.get('played_s', 0):5.0f}s" if end else "     ?"
        code = (end or {}).get("code", "CUT SHORT" if g.get("cut_short") else "no end")
        winner = (end or {}).get("winner") or "-"
        avg = f"{statistics.mean(totals):.0f}/{max(totals)}" if totals else "-"
        print(f" {i:2d}  {s['t'][11:19]}  {played}  {code:11s}  {winner:11s}  {len(real):5d} {res['hit']:3d} {res['miss']:4d} {res['one_barrel']:4d} "
              f"{res['no_ack'] + res['shooter_offline']:5d} {res['stale'] + res['queue_full']:5d} {res['too_soon']:4d} | {avg:>14s} | {sum(silent.values()):6d} | "
              f"{len(g['tank_drops'])}/{len(g['phone_drops'])}")
        tag = f"game {i} ({s['t'][11:19]})"
        if g.get("cut_short"):
            flags.append(f"{tag}: the hub restarted during it")
        if not s.get("tanks") or any(not t.get("online", True) for t in s.get("tanks", [])):
            off = [t["tank"] for t in s.get("tanks", []) if not t.get("online", True)]
            if off:
                flags.append(f"{tag}: started with {', '.join(off)} not connected")
        if s.get("started_despite_warnings"):
            flags.append(f"{tag}: the host started it past a warning (a player without a tank, or a tank check: see the start_check event just before)")
        if res["stale"] or res["queue_full"]:
            flags.append(f"{tag}: {res['stale'] + res['queue_full']} shot(s) dropped for waiting too long behind other shots")
        if real and (res["no_ack"] + res["shooter_offline"]) > max(1, len(real) // 50):
            who = Counter(x["tank"] for x in real if x.get("result") in ("no_ack", "shooter_offline"))
            flags.append(f"{tag}: {res['no_ack'] + res['shooter_offline']} shot(s) where the shooter's tank didn't answer ({dict(who)})")
        if silent and sum(silent.values()) > max(2, len(real) // 20):
            flags.append(f"{tag}: listening tanks answered late or not at all {sum(silent.values())} times ({dict(silent)}): each one stretches that shot by the full timeout")
        if offline:
            flags.append(f"{tag}: tanks not connected during shots: {dict(offline)}")
        if res["too_soon"] > 2:
            flags.append(f"{tag}: {res['too_soon']} shot(s) refused for arriving before the reload time was up")
        if totals and max(totals) > 1500:
            flags.append(f"{tag}: slowest shot took {max(totals)} ms from trigger to result")
        for d in g["tank_drops"]:
            flags.append(f"{tag}: {d['tank']} dropped off at {d['t'][11:19]} ({d.get('why')}; silent {d.get('silent_ms', 0)} ms)")
        for d in g["phone_drops"]:
            flags.append(f"{tag}: {d['player']}'s phone dropped at {d['t'][11:19]}" + (" (couldn't keep up with the hub)" if d.get("too_slow") else ""))
        # Video while the game was played.
        if end and "play" in g and by_time:
            a, b = tod(g["play"]["t"]), tod(end["t"])
            per = {}
            for tank in g["playing"]:
                rows = [r for t, r in by_time.get(tank, {}).items() if a + 4 <= tod(t) <= b]
                fps = [num(r.get("fps_in")) for r in rows if num(r.get("fps_in")) is not None]
                if fps:
                    gaps = [num(r.get("max_gap_ms"), 0) for r in rows]
                    send = [num(r.get("tank_send_ms_avg")) for r in rows if num(r.get("tank_send_ms_avg")) is not None]
                    rssi = [num(r.get("rssi_dbm")) for r in rows if num(r.get("rssi_dbm")) is not None]
                    per[tank] = (statistics.mean(fps), min(fps), max(gaps), statistics.mean(send) if send else None,
                                 statistics.mean(rssi) if rssi else None)
            if per:
                typical = statistics.median(v[0] for v in per.values())
                line = "       video fps avg (lowest, longest gap ms): " + ", ".join(
                    f"{t} {v[0]:.0f} ({v[1]:.0f}, {v[2]:.0f})" for t, v in sorted(per.items()))
                print(line)
                for t, v in sorted(per.items()):
                    if v[0] < 0.8 * typical or v[2] >= 1000:
                        extra = f", sends {v[3]:.0f} ms" if v[3] is not None else ""
                        extra += f", signal {v[4]:.0f} dBm" if v[4] is not None else ""
                        flags.append(f"{tag}: {t}'s video averaged {v[0]:.0f} fps (others about {typical:.0f}), longest gap {v[2]:.0f} ms{extra}")

    # ── Tanks over the session ───────────────────────────────────────────────
    tanks = defaultdict(lambda: defaultdict(list))
    for e in events:
        t = e.get("tank")
        if t and e["ev"] in ("tank", "tank_connect", "tank_disconnect", "camera_start", "video_end", "rfid_reader"):
            tanks[t][e["ev"]].append(e)
    if tanks:
        print("\nTANKS over the session (memory in KB: lowest free in each 30 s, first -> last)")
    for name in sorted(tanks, key=lambda n: (len(n), n)):
        t = tanks[name]
        h = t["tank"]
        line = f"  {name}: connected {len(t['tank_connect'])}x, dropped {len(t['tank_disconnect'])}x"
        boots = [c for c in t["tank_connect"] if c.get("up_s", 0) < 60 and c.get("reset")]
        if boots:
            line += f", started {len(boots)}x ({', '.join(c['reset'] for c in boots)})"
        print(line)
        for c in boots:
            if c["reset"] in ("crash", "watchdog", "brownout"):
                flags.append(f"{name} restarted by itself at about {c['t'][11:19]}: {c['reset']}" + (f" ({c['crash']})" if c.get("crash") else ""))
        fw = {c.get("fw") for c in t["tank_connect"] if c.get("fw")}
        if h:
            heap = [x["heap_low_kb"] for x in h if "heap_low_kb" in x]
            blk = [x["blk_low_kb"] for x in h if "blk_low_kb" in x]
            loop = [x["loop_max_ms"] for x in h if "loop_max_ms" in x]
            gap = [x.get("hb_gap_max_ms", 0) for x in h]
            dropped = sum(x.get("cmds_dropped", 0) for x in h)
            temp = [x["temp_c"] for x in h if "temp_c" in x]
            if heap:
                print(f"      free memory {first_last_low(heap)}; largest block {first_last_low(blk) if blk else '?'}")
                if len(blk) > 10 and blk[-1] < 0.6 * statistics.median(blk[:5]):
                    flags.append(f"{name}'s largest free block of memory fell from about {statistics.median(blk[:5]):.0f} to {blk[-1]:.0f} KB: its memory is breaking up")
                if min(heap) < 40:
                    flags.append(f"{name}'s free memory got down to {min(heap):.0f} KB")
            extra = []
            if loop:
                extra.append(f"longest main-loop hold-up {max(loop)} ms")
                if max(loop) >= 250:
                    flags.append(f"{name}'s main loop was held up for {max(loop)} ms at some point (motor control and IR timing stop meanwhile)")
            else:
                extra.append("old firmware (no loop figure)")
            extra.append(f"longest wait between heartbeats {max(gap)} ms")
            if max(gap) >= 5000:
                flags.append(f"{name} went {max(gap)} ms between heartbeats at some point (they are 2 s apart)")
            if temp:
                extra.append(f"chip up to {max(temp):.0f} C")
            if dropped:
                extra.append(f"{dropped} commands dropped")
                flags.append(f"{name}: the hub dropped {dropped} commands because it wasn't taking them")
            ping = [x["ping_ms"] for x in h if "ping_ms" in x]
            ping_max = [x["ping_max_ms"] for x in h if "ping_max_ms" in x]
            lost = sum(x.get("pings_lost", 0) for x in h)
            if ping:
                extra.append(f"ping {statistics.median(ping):.0f} ms typical, {max(ping_max)} ms worst" + (f", {lost} unanswered" if lost else ""))
                if max(ping_max) >= 500:
                    flags.append(f"{name}: a command took {max(ping_max)} ms to get there and back at some point (typical {statistics.median(ping):.0f} ms)")
                if lost > 2:
                    flags.append(f"{name}: {lost} of the hub's pings went unanswered")
            for key, label in (("wifi_drops", "Wi-Fi drops since it started"), ("hub_connects", "hub connections since it started"),
                               ("ir_barrel_left_on", "IR BARREL LEFT ON")):
                v = [x[key] for x in h if key in x]
                if v:
                    extra.append(f"{label} {max(v)}")
            if any("ir_barrel_left_on" in x for x in h):
                flags.append(f"{name}: an IR barrel was left lit and put itself out (its stop command went missing)")
            print("      " + "; ".join(extra))
        bad_cam = [c for c in t["camera_start"] if not c.get("ok") or c.get("tries", 1) > 1]
        if bad_cam:
            flags.append(f"{name}: camera start trouble {len(bad_cam)}x (failed {sum(1 for c in bad_cam if not c.get('ok'))}x)")
        if len(t["video_end"]) > 2:
            flags.append(f"{name}: its video stream broke and was reopened {len(t['video_end'])} times")
        if len(fw) > 1:
            flags.append(f"{name} ran more than one firmware build: {sorted(fw)}")
    # ── Phones ───────────────────────────────────────────────────────────────
    phones = defaultdict(list)
    for e in events:
        if e["ev"] == "phone" and e.get("player"):
            phones[e["player"]].append(e)
    if phones:
        print("\nPHONES (from each phone's own 30 s reports while playing)")
    for name in sorted(phones):
        r = phones[name]
        shown = [x["shown_fps"] for x in r if "shown_fps" in x]
        rtt = [x["rtt_ms"] for x in r if x.get("rtt_n")]
        rtt_max = [x["rtt_max_ms"] for x in r if x.get("rtt_n")]
        line = f"  {name}: video shown {statistics.mean(shown):.0f} fps avg (lowest {min(shown):.0f})" if shown else f"  {name}:"
        if rtt:
            line += f"; link to the hub {statistics.median(rtt):.0f} ms typical, {max(rtt_max):.0f} ms worst"
            if max(rtt_max) >= 500:
                flags.append(f"{name}'s phone took {max(rtt_max):.0f} ms to reach the hub and back at some point (typical {statistics.median(rtt):.0f} ms): its Wi-Fi, not the tank")
        line += f"; {r[-1].get('device', '')[:60]}"
        print(line)
        if shown and statistics.mean(shown) < 12:
            flags.append(f"{name}'s phone showed only {statistics.mean(shown):.0f} fps on average")

    builds = Counter(c.get("fw") for e in events if e["ev"] == "tank_connect" for c in [e] if c.get("fw"))
    if len(builds) > 1:
        flags.append(f"the tanks aren't all on the same firmware: {dict(builds)}")

    print("\nWORTH A LOOK:" if flags else "\nNothing out of the ordinary flagged.")
    for f in flags:
        print("  - " + f)


if __name__ == "__main__":
    main()
