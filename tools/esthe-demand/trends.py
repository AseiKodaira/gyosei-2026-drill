"""タスク1：Googleトレンドで検索量の月別の波を調べる。

使い方:
  python trends.py            # pytrends で自動取得（失敗したら手動CSVを探す）
  python trends.py --csv      # 手動で置いたCSVだけを使う
  python trends.py --hourly   # （任意）直近7日の1時間ごとの検索量を記録に追加する

出力（~/esthe_data/ に保存）:
  trends_raw.csv            取得した元データ（週ごと）
  trends_month_rank.csv     月別平均と順位
  trends_monthly.png        月別の折れ線グラフ
"""
import argparse
import re
import sys
import time
from datetime import datetime
from pathlib import Path

import pandas as pd

from common import load_config, setup_japanese_font

MONTH_LABELS = [f"{m}月" for m in range(1, 13)]


# ---------------------------------------------------------------- 取得
def fetch_with_pytrends(keywords, timeframe="today 5-y"):
    from pytrends.request import TrendReq

    # tz=-540 は日本時間（UTC+9）の指定
    pytrends = TrendReq(hl="ja-JP", tz=-540, timeout=(10, 30))
    last_error = None
    for attempt in range(3):
        try:
            pytrends.build_payload(keywords, timeframe=timeframe, geo="JP")
            df = pytrends.interest_over_time()
            if df is None or df.empty:
                raise RuntimeError("Googleトレンドから空のデータが返りました（検索量が少なすぎる可能性）")
            return df.drop(columns=["isPartial"], errors="ignore")
        except Exception as e:  # 429（アクセス過多）などは少し待って再試行
            last_error = e
            wait = 20 * (attempt + 1)
            print(f"  取得に失敗しました（{e.__class__.__name__}: {e}）。{wait}秒待って再試行します…")
            time.sleep(wait)
    raise RuntimeError(f"pytrends での取得に3回失敗しました: {last_error}")


def _to_number(v):
    s = str(v).strip()
    if s in ("", "nan"):
        return None
    if s.startswith("<"):  # "<1" は「ほぼゼロ」なので 0.5 として扱う
        return 0.5
    try:
        return float(s)
    except ValueError:
        return None


def read_manual_csv(path):
    """Googleトレンドの「ダウンロード」ボタンで保存したCSVを読む（日本語・英語どちらの画面でも可）。"""
    lines = Path(path).read_text(encoding="utf-8-sig").splitlines()
    header_idx = None
    for i, line in enumerate(lines):
        first = line.split(",")[0].strip().strip('"')
        if first in ("週", "月", "日", "日付", "時間", "Week", "Month", "Day", "Time"):
            header_idx = i
            break
    if header_idx is None:
        raise ValueError(f"{path.name}: 見出し行（週 / Week など）が見つかりません")
    from io import StringIO
    df = pd.read_csv(StringIO("\n".join(lines[header_idx:])), dtype=str)
    date_col = df.columns[0]
    df[date_col] = pd.to_datetime(df[date_col], errors="coerce")
    df = df.dropna(subset=[date_col]).set_index(date_col)
    df.index.name = "date"
    rename = {}
    for c in df.columns:
        rename[c] = re.sub(r":\s*\(.*\)\s*$", "", c).strip()  # "上野 メンズエステ: (日本)" -> "上野 メンズエステ"
    df = df.rename(columns=rename)
    for c in df.columns:
        df[c] = df[c].map(_to_number)
    return df.astype(float)


def load_manual_csvs(folder):
    files = sorted(folder.glob("*.csv"))
    if not files:
        return None
    frames = []
    for f in files:
        try:
            frames.append(read_manual_csv(f))
            print(f"  読み込み: {f.name}")
        except Exception as e:
            print(f"  ※ {f.name} は読み込めませんでした: {e}")
    if not frames:
        return None
    df = pd.concat(frames, axis=1)
    df = df.loc[:, ~df.columns.duplicated(keep="last")]
    return df.sort_index()


def print_csv_guide(folder, keywords):
    print(f"""
================ 手動でCSVを用意する方法 ================
自動取得がうまくいかないときは、ブラウザで次の手順を行ってください。

 1. https://trends.google.co.jp/trends/explore?geo=JP&date=today%205-y を開く
 2. 検索語に次のキーワードを入れる（「比較を追加」で並べてもOK）
      {' / '.join(keywords)}
 3. 地域「日本」・期間「過去5年間」になっていることを確認
 4. 「時系列」グラフ右上の ↓（ダウンロード）ボタンで CSV を保存
 5. 保存した CSV を次のフォルダに入れる（ファイル名は何でもOK・複数可）
      {folder}
    ターミナルなら例えば:
      mkdir -p {folder}
      mv ~/Downloads/multiTimeline*.csv {folder}/
 6. もう一度実行:
      python trends.py --csv
=========================================================
""")


# ---------------------------------------------------------------- 集計
def monthly_rank(df):
    """月（1〜12月）ごとの平均を出し、高い順に順位をつける。"""
    rows = []
    for kw in df.columns:
        s = df[kw].dropna()
        if s.empty or s.mean() < 1:  # ほとんど検索されていない語は、ばらつきが大きく当てにならないので除く
            continue
        by_month = s.groupby(s.index.month).mean()
        overall = s.mean()
        ranks = by_month.rank(ascending=False, method="min").astype(int)
        for m, v in by_month.items():
            rows.append({"キーワード": kw, "月": int(m), "平均": round(v, 2),
                         "年平均との差(%)": round((v / overall - 1) * 100, 1) if overall else 0,
                         "順位": int(ranks[m])})
    return pd.DataFrame(rows)


def plot(df, rank_df, out_png):
    import matplotlib.pyplot as plt
    setup_japanese_font()
    kws = list(rank_df["キーワード"].unique())
    fig, axes = plt.subplots(2, 1, figsize=(11, 9))

    ax = axes[0]
    for kw in kws:
        sub = rank_df[rank_df["キーワード"] == kw].sort_values("月")
        ax.plot(sub["月"], sub["平均"], marker="o", linewidth=2, label=kw)
    ax.set_xticks(range(1, 13))
    ax.set_xticklabels(MONTH_LABELS)
    ax.set_title("月ごとの検索量の平均（過去5年をまとめたもの）")
    ax.set_ylabel("検索の多さ（最大=100の目安）")
    ax.grid(alpha=0.3)
    ax.legend()

    ax = axes[1]
    monthly = df[kws].resample("MS").mean()
    for kw in kws:
        ax.plot(monthly.index, monthly[kw], linewidth=1.6, label=kw)
    ax.set_title("月ごとの検索量の推移（5年間）")
    ax.set_ylabel("検索の多さ")
    ax.grid(alpha=0.3)
    ax.legend()

    fig.tight_layout()
    fig.savefig(out_png, dpi=130)
    plt.close(fig)


def summarize(rank_df):
    print("\n========== 月別ランキング（検索が多い順） ==========")
    for kw in rank_df["キーワード"].unique():
        sub = rank_df[rank_df["キーワード"] == kw].sort_values("順位")
        top = "、".join(f"{m}月" for m in sub.head(3)["月"])
        low = "、".join(f"{m}月" for m in sub.tail(3).sort_values("順位", ascending=False)["月"])
        print(f"\n■ {kw}")
        print(f"  多い月 ベスト3: {top}")
        print(f"  少ない月 ワースト3: {low}")
        for _, r in sub.iterrows():
            sign = "+" if r["年平均との差(%)"] >= 0 else ""
            print(f"   {int(r['順位']):>2}位 {int(r['月']):>2}月  平均 {r['平均']:>6.1f}"
                  f"（年平均より {sign}{r['年平均との差(%)']}%）")
    print("\n※ 数字は Google の検索の「多さの目安」で、来店数ではありません。")


# ---------------------------------------------------------------- 時間帯（任意）
def hourly(cfg):
    keywords = cfg["trends_keywords"]
    print("直近7日の1時間ごとの検索量を取得します…")
    df = fetch_with_pytrends(keywords, timeframe="now 7-d")
    # pytrends の時刻は世界標準時なので日本時間に直す
    df.index = df.index + pd.Timedelta(hours=9)
    long = df.reset_index().melt(id_vars=df.index.name or "date", var_name="キーワード", value_name="値")
    long = long.rename(columns={long.columns[0]: "日時"})
    long.insert(0, "取得日時", datetime.now().strftime("%Y-%m-%d %H:%M"))
    out = cfg["data_dir"] / "trends_hourly_log.csv"
    is_new = not out.exists()
    # 追記時に BOM を重ねないよう、新規作成のときだけ utf-8-sig（Excelで文字化けしない形式）にする
    long.to_csv(out, mode="a", header=is_new, index=False, encoding="utf-8-sig" if is_new else "utf-8")
    print(f"記録しました: {out}（{len(long)}行）")


# ---------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", action="store_true", help="手動で置いたCSVだけを使う")
    ap.add_argument("--hourly", action="store_true", help="直近7日の時間別データを記録する（任意）")
    args = ap.parse_args()

    cfg = load_config()
    data_dir = cfg["data_dir"]
    csv_dir = data_dir / "trends_csv"
    keywords = cfg["trends_keywords"]

    if args.hourly:
        try:
            hourly(cfg)
        except Exception as e:
            print(f"時間別データの取得に失敗しました: {e}\n（この機能は任意です。失敗しても他の分析は動きます）")
        return

    df = None
    if not args.csv:
        print(f"Googleトレンドから取得します（日本・過去5年）: {', '.join(keywords)}")
        try:
            df = fetch_with_pytrends(keywords)
            print("  取得できました。")
        except Exception as e:
            print(f"\n自動取得できませんでした: {e}")
            print("手動CSVを探します…")
    if df is None:
        df = load_manual_csvs(csv_dir)
        if df is None:
            print_csv_guide(csv_dir, keywords)
            sys.exit(1)

    df.to_csv(data_dir / "trends_raw.csv", encoding="utf-8-sig")
    rank_df = monthly_rank(df)
    if rank_df.empty:
        print("どのキーワードも検索量がほぼゼロでした。キーワードを変えて試してください（config.json の trends_keywords）。")
        sys.exit(1)
    skipped = [k for k in df.columns if k not in set(rank_df["キーワード"])]
    if skipped:
        print(f"※ 検索量が少なすぎて集計できなかったキーワード: {', '.join(skipped)}")

    rank_df.to_csv(data_dir / "trends_month_rank.csv", index=False, encoding="utf-8-sig")
    plot(df, rank_df, data_dir / "trends_monthly.png")
    summarize(rank_df)
    print(f"\n保存しました:\n  {data_dir / 'trends_month_rank.csv'}\n  {data_dir / 'trends_monthly.png'}")


if __name__ == "__main__":
    main()
