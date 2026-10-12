"""Local build handbook worker. Reads cached game data; never calls a model or network."""
from __future__ import annotations

import argparse
import ctypes
from datetime import datetime, timezone
import json
import logging
import os
from pathlib import Path
import sqlite3
import sys
import time

from catalog import Catalog, atomic_json, default_cache, read_json
from strategy import StrategyBook

ROOT = Path(__file__).resolve().parent
DOCUMENT_KIND = "karnok-build-reference-v4"


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def snapshot_age(snapshot):
    try:
        return (datetime.now(timezone.utc) - datetime.fromisoformat(snapshot["captured_at_utc"].replace("Z", "+00:00"))).total_seconds()
    except (KeyError, ValueError, TypeError):
        return float("inf")


def make_document(book, snapshot, *, catalog_ok=True):
    snapshot = snapshot if isinstance(snapshot, dict) else {}
    fresh = -5 <= snapshot_age(snapshot) <= 4
    reference = book.reference(snapshot if fresh and catalog_ok else {}, catalog_ok=catalog_ok) if book else None
    return {"kind": DOCUMENT_KIND, "snapshot_id": snapshot.get("snapshot_id", ""), "reference": reference,
            "status": "catalog_error" if not catalog_ok else "stale" if not fresh else "live" if reference and reference["live"] else "browse",
            "generated_at_utc": utc_now()}


def catalog_stamp(cache):
    def stamp(path):
        try:
            stat = path.stat()
            return (str(path), stat.st_mtime_ns, stat.st_size)
        except FileNotFoundError:
            return (str(path), None, None)
    return tuple(stamp(cache / name) for name in ("GameData.db", "translations/zh-CN.bytes"))


def lock_worker(runtime):
    stream = (runtime / "guide-worker.lock").open("a+b")
    if stream.tell() == 0: stream.write(b"0"); stream.flush()
    stream.seek(0)
    try:
        if sys.platform == "win32":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        stream.close()
        return None
    return stream


class ParentProcess:
    def __init__(self, pid):
        self.handle = None
        if pid and sys.platform == "win32":
            self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            self.kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
            self.kernel.OpenProcess.restype = ctypes.c_void_p
            self.kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
            self.kernel.WaitForSingleObject.restype = ctypes.c_ulong
            self.kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            self.handle = self.kernel.OpenProcess(0x00100000, False, pid)
            if not self.handle: raise RuntimeError("无法观察游戏进程生命周期")

    def alive(self):
        return self.handle is None or self.kernel.WaitForSingleObject(self.handle, 0) == 258

    def close(self):
        if self.handle: self.kernel.CloseHandle(self.handle)


def serve(runtime, parent_pid=0, settings_path=ROOT / "settings.json", *, max_seconds=0):
    runtime.mkdir(parents=True, exist_ok=True)
    lock = lock_worker(runtime)
    for _ in range(12):
        if lock is not None: break
        time.sleep(.1)
        lock = lock_worker(runtime)
    if lock is None: return
    logger = logging.getLogger("karnok-guide." + str(runtime))
    logger.setLevel(logging.INFO)
    logger.propagate = False
    handler = logging.FileHandler(runtime / "guide-worker.log", encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)
    parent = None
    book, loaded_stamp = None, None
    last_reload, last_write = -10.0, -10.0
    catalog_ok = False
    start = time.monotonic()
    try:
        parent = ParentProcess(parent_pid)
        while parent.alive() and (not max_seconds or time.monotonic() - start < max_seconds):
            now = time.monotonic()
            if now - last_reload >= 5:
                last_reload = now
                try:
                    settings = read_json(settings_path) if settings_path.exists() else {}
                    if not isinstance(settings, dict): raise ValueError("Settings must be a JSON object")
                    cache = Path(settings["cache_dir"]).resolve() if settings.get("cache_dir") else default_cache()
                    stamp = catalog_stamp(cache)
                    if book is None or stamp != loaded_stamp or not catalog_ok:
                        candidate = StrategyBook(Catalog(cache))
                        book, loaded_stamp = candidate, stamp
                        logger.info("Local handbook loaded: templates=%s", len(book.templates))
                    catalog_ok = True
                except (OSError, ValueError, KeyError, sqlite3.Error) as error:
                    catalog_ok = False
                    logger.warning("Local catalog unavailable: %s", type(error).__name__)
            try:
                snapshot = read_json(runtime / "snapshot.json")
            except (OSError, ValueError):
                snapshot = {}
            if now - last_write >= .5:
                try:
                    atomic_json(runtime / "guide.json", make_document(book, snapshot, catalog_ok=catalog_ok))
                    atomic_json(runtime / "guide-heartbeat.json", {"kind": DOCUMENT_KIND, "pid": os.getpid(), "parent_pid": parent_pid,
                                                                  "at": utc_now(), "version": "0.5.1"})
                    last_write = now
                except OSError as error:
                    logger.warning("Local guide publication deferred: %s", type(error).__name__)
            time.sleep(.25)
    finally:
        if parent: parent.close()
        lock.close()
        logger.removeHandler(handler)
        handler.close()


def main():
    parser = argparse.ArgumentParser(description="卡诺克阵容手册：读取本机卡牌和实时持有状态，提供阵容参考。")
    parser.add_argument("--runtime", type=Path, default=ROOT / "runtime")
    parser.add_argument("--settings", type=Path, default=ROOT / "settings.json")
    parser.add_argument("--parent-pid", type=int, default=0)
    parser.add_argument("--once", type=Path, help="为指定状态 JSON 输出一次本地阵容参考")
    args = parser.parse_args()
    if args.once:
        settings = read_json(args.settings) if args.settings.exists() else {}
        book = StrategyBook(Catalog(Path(settings["cache_dir"]) if settings.get("cache_dir") else None))
        print(json.dumps(make_document(book, read_json(args.once)), ensure_ascii=False, indent=2))
    else:
        serve(args.runtime, args.parent_pid, args.settings)


if __name__ == "__main__":
    if sys.stdout: sys.stdout.reconfigure(encoding="utf-8")
    main()
