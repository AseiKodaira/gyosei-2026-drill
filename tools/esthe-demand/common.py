"""共通の設定読み込み・グラフ用フォント設定など。"""
import json
import os
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load_config():
    path = Path(os.environ.get("ESTHE_CONFIG", HERE / "config.json"))  # テスト用に差し替え可能
    with open(path, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg["data_dir"] = Path(os.path.expanduser(cfg["data_dir"]))
    cfg["data_dir"].mkdir(parents=True, exist_ok=True)
    return cfg


def setup_japanese_font():
    """Mac に入っている日本語フォントをグラフに使う（文字化け防止）。"""
    import matplotlib
    matplotlib.use("Agg")
    from matplotlib import font_manager, rcParams

    candidates = ["Hiragino Sans", "Hiragino Kaku Gothic ProN", "Hiragino Maru Gothic Pro",
                  "YuGothic", "Yu Gothic", "AppleGothic", "Noto Sans CJK JP",
                  "IPAexGothic", "WenQuanYi Zen Hei"]
    installed = {f.name for f in font_manager.fontManager.ttflist}
    for name in candidates:
        if name in installed:
            rcParams["font.family"] = name
            break
    rcParams["axes.unicode_minus"] = False


def hhmm_to_minutes(text, last_time="29:00"):
    """'12:00' -> 720, '27:00' -> 1620, 'LAST' -> last_time の分数。読めなければ None。"""
    if text is None:
        return None
    t = str(text).strip()
    if t.upper() in ("LAST", "L") or t in ("ラスト", "ラスト迄", "ラストまで"):
        t = last_time
    if ":" not in t:
        return None
    h, m = t.split(":", 1)
    try:
        return int(h) * 60 + int(m)
    except ValueError:
        return None
