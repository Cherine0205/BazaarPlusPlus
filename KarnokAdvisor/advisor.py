"""Companion process. File IPC keeps model and database work off Unity's main thread."""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import copy
import ctypes
from datetime import datetime, timezone
import json
import logging
from pathlib import Path
import sys
import time

from catalog import Catalog, atomic_json, default_cache, read_json
from engine import Engine
from model_api import request_ranking

ROOT = Path(__file__).resolve().parent


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def snapshot_age(snapshot: dict) -> float:
    try:
        timestamp = datetime.fromisoformat(snapshot["captured_at_utc"].replace("Z", "+00:00"))
        return (datetime.now(timezone.utc) - timestamp).total_seconds()
    except (KeyError, ValueError, TypeError):
        return float("inf")


def model_context(engine: Engine, snapshot: dict) -> dict:
    state = engine.prepare(snapshot)
    result = {k: state.get(k) for k in ("hero", "day", "hour", "victories", "losses", "player_attributes", "state_type", "selection_rules")}
    for section in ("board", "stash", "skills", "options"):
        result[section] = [{**c, "effects": engine.catalog.texts(c)} for c in state[section]]
    guide = engine.strategies.reference(state)
    def current_stage(route):
        stage = next((s for s in route["stages"] if s["key"] == guide["stage"]), None)
        return {**stage, "items": [{k: v for k, v in item.items() if k != "art_key"} for item in stage["items"]]} if stage else None

    result["strategy_reference"] = {k: v for k, v in guide.items() if k != "routes"}
    result["strategy_reference"]["routes"] = [
        {k: route[k] for k in ("name", "signal", "status", "owned_board", "owned_stash", "missing_roles", "pivot", "caution", "skills", "sources")}
        | {"current_stage": current_stage(route)}
        for route in guide["routes"]]
    return result


def lock_worker(runtime: Path):
    stream = (runtime / "worker.lock").open("a+b")
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
    def __init__(self, pid: int):
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

    def alive(self) -> bool:
        return self.handle is None or self.kernel.WaitForSingleObject(self.handle, 0) == 258

    def close(self):
        if self.handle: self.kernel.CloseHandle(self.handle)


def serve(runtime: Path, parent_pid: int = 0, settings_path: Path = ROOT / "settings.json", *, max_seconds: float = 0):
    runtime.mkdir(parents=True, exist_ok=True)
    lock = lock_worker(runtime)
    if lock is None: return
    logger = logging.getLogger("karnok-worker." + str(runtime))
    logger.setLevel(logging.INFO)
    logger.propagate = False
    handler = logging.FileHandler(runtime / "worker.log", encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)
    parent = ParentProcess(parent_pid)
    start = time.monotonic()
    current_id, requested_id, config_text = "", "", ""
    local, snapshot, settings, future = {}, {}, {}, None
    last_change, last_write, last_reload = 0.0, 0.0, 0.0
    catalog_stamp = None
    generation = 0
    engine = None
    pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="optional-model")
    try:
        while parent.alive() and (not max_seconds or time.monotonic() - start < max_seconds):
            now = time.monotonic()
            try:
                text = settings_path.read_text(encoding="utf-8-sig")
                if text != config_text:
                    settings = json.loads(text)
                    config_text = text
                    requested_id, current_id = "", ""
                    generation += 1
                cache = Path(settings["cache_dir"]) if settings.get("cache_dir") else None
                if engine is None or now - last_reload > 5:
                    path = (cache or default_cache()) / "GameData.db"
                    stamp = (str(path), path.stat().st_mtime_ns, path.stat().st_size)
                    if stamp != catalog_stamp:
                        engine = Engine(Catalog(cache))
                        catalog_stamp = stamp
                        current_id = ""
                        generation += 1
                        logger.info("Catalog loaded: templates=%s", len(engine.catalog.cards))
                    last_reload = now
                snapshot_path = runtime / "snapshot.json"
                if snapshot_path.exists(): snapshot = read_json(snapshot_path)
                sid = snapshot.get("snapshot_id", "")
                age = snapshot_age(snapshot)
                if not sid or age > 4 or age < -5:
                    response = {"snapshot_id": sid, "summary": "未收到实时游戏状态，暂停推荐", "recommendations": [],
                                "model_status": "等待游戏连接", "notes": []}
                    atomic_json(runtime / "recommendation.json", {**response, "generated_at_utc": utc_now()})
                    current_id = ""
                    time.sleep(0.4)
                    continue
                if sid != current_id:
                    local = engine.recommend(snapshot)
                    local["catalog_etag"] = engine.catalog.etag
                    current_id, last_change = sid, now
                    with (runtime / "decisions.jsonl").open("a", encoding="utf-8") as log:
                        log.write(json.dumps({"at": utc_now(), "snapshot": snapshot, "decision": local}, ensure_ascii=False) + "\n")
                if future is not None and future.done():
                    old_id, task = future.snapshot_id, future
                    future = None
                    if old_id == current_id and task.generation == generation and settings.get("model_api", {}).get("enabled"):
                        try:
                            ranked = task.result()
                            local["recommendations"] = ranked
                            local["summary"] = "优先：" + ranked[0]["title"]
                            local["model_status"] = "模型策略排序 · 本地事实和可执行性校验已保留"
                        except Exception as error:
                            local["model_status"] = "模型暂不可用，继续显示本地规则估算（" + type(error).__name__ + "）"
                            logger.warning("Model reranking failed: %s", type(error).__name__)
                model = settings.get("model_api", {})
                if model.get("enabled") and any(c["eligible"] for c in local.get("candidates", [])):
                    if future is None and requested_id != current_id and now - last_change >= 1.0:
                        requested_id = current_id
                        local["model_status"] = "模型分析中，先显示本地规则估算"
                        future = pool.submit(request_ranking, copy.deepcopy(model), copy.deepcopy(snapshot), copy.deepcopy(local), model_context(engine, snapshot))
                        future.snapshot_id = current_id
                        future.generation = generation
                elif not model.get("enabled"):
                    local["model_status"] = "本地规则估算 · 模型接口预留，当前未启用"
                if now - last_write >= 0.5:
                    atomic_json(runtime / "recommendation.json", {**local, "generated_at_utc": utc_now()})
                    atomic_json(runtime / "worker-heartbeat.json", {"pid": __import__("os").getpid(), "at": utc_now(), "version": "0.1.0"})
                    last_write = now
            except (OSError, ValueError, KeyError) as error:
                logger.warning("State/configuration unavailable: %s; file=%s; winerror=%s",
                               type(error).__name__, getattr(error, "filename", None), getattr(error, "winerror", None))
                try:
                    atomic_json(runtime / "recommendation.json", {"snapshot_id": snapshot.get("snapshot_id", ""),
                        "summary": "状态或配置读取失败，已停止推荐", "recommendations": [], "notes": [],
                        "model_status": type(error).__name__, "generated_at_utc": utc_now()})
                except OSError as publication_error:
                    logger.warning("Could not publish status; will retry: %s", type(publication_error).__name__)
                current_id = ""
            time.sleep(0.25)
    finally:
        pool.shutdown(wait=False, cancel_futures=True)
        parent.close()
        lock.close()
        logger.removeHandler(handler)
        handler.close()


def main():
    parser = argparse.ArgumentParser(description="卡诺克实时选择助手：从独立游戏插件读取状态，只输出建议。")
    parser.add_argument("--runtime", type=Path, default=ROOT / "runtime")
    parser.add_argument("--settings", type=Path, default=ROOT / "settings.json")
    parser.add_argument("--parent-pid", type=int, default=0)
    parser.add_argument("--once", type=Path, help="对指定状态 JSON 执行一次本地分析，不调用模型")
    args = parser.parse_args()
    if args.once:
        settings = json.loads(args.settings.read_text(encoding="utf-8-sig"))
        engine = Engine(Catalog(Path(settings["cache_dir"]) if settings.get("cache_dir") else None))
        print(json.dumps(engine.recommend(json.loads(args.once.read_text(encoding="utf-8-sig"))), ensure_ascii=False, indent=2))
    else: serve(args.runtime, args.parent_pid, args.settings)


if __name__ == "__main__":
    if sys.stdout: sys.stdout.reconfigure(encoding="utf-8")
    main()
