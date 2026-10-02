"""タスク4：たまったデータを集計して、増員を考えるための資料を作る。

使い方:
  python analyze.py

読み込むもの（~/esthe_data/）:
  schedule_log.csv           （必須）タスク2の記録
  trends_month_rank.csv      （あれば）タスク1の月別ランキング
  availability_log.csv       （あれば）タスク3の空き状況
  trends_hourly_log.csv      （あれば）時間別の検索量

出力（~/esthe_data/report/）:
  report.md                  日本語のまとめ（テキストエディタやブラウザで開けます）
  heatmap_weekday_hour.png   曜日×時間帯の平均出勤人数
  weekday_hour_table.csv     上の表の数値（Excelで開けます）

※ キャストの名前は数を数えるためだけに使い、結果には一切出しません。
"""
import sys
from datetime import date, datetime

import numpy as np
import pandas as pd

from common import hhmm_to_minutes, load_config, setup_japanese_font

WEEKDAYS = ["月", "火", "水", "木", "金", "土", "日"]
BANDS = [("昼（10〜15時）", 10, 15), ("夕方（15〜19時）", 15, 19),
         ("夜（19〜24時）", 19, 24), ("深夜（0〜5時）", 24, 29)]
SOLD_OUT_WORDS = ["受付終了", "完売", "満了", "満枠", "予約満了"]


def hour_label(h):
    return f"{h}時" if h < 24 else f"翌{h - 24}時"


# ---------------------------------------------------------------- 出勤データ
def load_schedule(path, cfg):
    df = pd.read_csv(path, encoding="utf-8-sig", dtype=str)
    df.columns = [c.strip() for c in df.columns]
    df["記録日時"] = pd.to_datetime(df["記録日時"], errors="coerce")
    df["対象日"] = pd.to_datetime(df["対象日"], errors="coerce").dt.date
    df = df.dropna(subset=["記録日時", "対象日"])
    # 同じ日について何度も記録しているので、いちばん新しい記録（当日に近い＝確定に近い）だけ使う
    latest = df.groupby("対象日")["記録日時"].transform("max")
    df = df[df["記録日時"] == latest]
    # まだ来ていない日は予定が変わりうるので除く
    df = df[df["対象日"] <= date.today()]
    df["開始分"] = df["開始"].map(lambda t: hhmm_to_minutes(t, cfg["last_time"]))
    df["終了分"] = df["終了"].map(lambda t: hhmm_to_minutes(t, cfg["last_time"]))
    df = df.dropna(subset=["開始分", "終了分"])
    df = df[df["終了分"] > df["開始分"]]
    return df.drop_duplicates(subset=["対象日", "キャスト名", "開始"])


def headcount_by_hour(df, hours):
    """日付ごと・時間ごとの出勤人数（各時間の30分の時点で出勤している人数）。"""
    rows = []
    for d, g in df.groupby("対象日"):
        row = {"対象日": d, "曜日": d.weekday(), "1日の出勤人数": g["キャスト名"].nunique()}
        for h in hours:
            t = h * 60 + 30
            row[h] = int(((g["開始分"] <= t) & (g["終了分"] > t)).sum())
        rows.append(row)
    return pd.DataFrame(rows)


def plot_heatmap(table, out_png, n_days):
    import matplotlib.pyplot as plt
    setup_japanese_font()
    fig, ax = plt.subplots(figsize=(max(10, 0.6 * table.shape[1] + 2), 5.2))
    im = ax.imshow(table.values, cmap="YlOrRd", aspect="auto")
    ax.set_xticks(range(table.shape[1]))
    ax.set_xticklabels(table.columns, rotation=0, fontsize=9)
    ax.set_yticks(range(table.shape[0]))
    ax.set_yticklabels(table.index)
    vmax = np.nanmax(table.values) if table.size else 0
    for i in range(table.shape[0]):
        for j in range(table.shape[1]):
            v = table.values[i, j]
            if np.isnan(v):
                continue
            ax.text(j, i, f"{v:.1f}", ha="center", va="center", fontsize=8,
                    color="white" if vmax and v > vmax * 0.6 else "black")
    ax.set_title(f"曜日×時間帯の平均出勤人数（{n_days}日分のデータ・推定用）")
    fig.colorbar(im, ax=ax, label="平均出勤人数（人）")
    fig.tight_layout()
    fig.savefig(out_png, dpi=130)
    plt.close(fig)


# ---------------------------------------------------------------- 他のデータ（任意）
def load_trend_months(path):
    if not path.exists():
        return None
    r = pd.read_csv(path, encoding="utf-8-sig")
    avg = r.groupby("月")["年平均との差(%)"].mean().sort_values(ascending=False)
    return avg, list(r["キーワード"].unique())


def load_soldout(path, schedule_dates):
    if not path.exists():
        return None
    a = pd.read_csv(path, encoding="utf-8-sig", dtype=str)
    a["記録日時"] = pd.to_datetime(a["記録日時"], errors="coerce")
    a["対象日"] = pd.to_datetime(a["対象日"], errors="coerce").dt.date
    a = a.dropna(subset=["記録日時", "対象日"])
    a = a[a["記録日時"] == a.groupby("対象日")["記録日時"].transform("max")]
    a = a[a["対象日"] <= date.today()]
    if a.empty:
        return None
    a["満了"] = a["状態"].map(lambda s: any(w in str(s) for w in SOLD_OUT_WORDS))
    # 空き状況の記録は「表示があった人」だけなので、表示がなかった日は 0 人として数える
    per_day = a.groupby("対象日")["満了"].sum().reindex(sorted(schedule_dates), fill_value=0)
    per_day = per_day.rename_axis("対象日").reset_index()
    per_day["曜日"] = per_day["対象日"].map(lambda d: d.weekday())
    return per_day.groupby("曜日")["満了"].mean()


def load_hourly_search(path):
    if not path.exists():
        return None
    h = pd.read_csv(path, encoding="utf-8-sig")
    h["日時"] = pd.to_datetime(h["日時"], errors="coerce")
    h = h.dropna(subset=["日時"])
    h["値"] = pd.to_numeric(h["値"], errors="coerce")
    h = h.sort_values("取得日時").drop_duplicates(subset=["日時", "キーワード"], keep="last")
    s = h.groupby("日時")["値"].sum()
    if s.empty or s.sum() == 0:
        return None
    df = s.reset_index()
    # 深夜0〜4時は「前日の続き」として 24〜28時 に寄せる（お店の営業日に合わせる）
    df["時"] = df["日時"].dt.hour
    df["営業日"] = df["日時"].dt.date
    early = df["時"] < 5
    df.loc[early, "時"] += 24
    df.loc[early, "営業日"] = (df.loc[early, "日時"] - pd.Timedelta(days=1)).dt.date
    df["曜日"] = df["営業日"].map(lambda d: d.weekday())
    days = df["営業日"].nunique()
    return df.groupby(["曜日", "時"])["値"].mean(), days


# ---------------------------------------------------------------- まとめ
def band_table(table_num, hours):
    out = pd.DataFrame(index=table_num.index)
    for name, a, b in BANDS:
        cols = [h for h in hours if a <= h < b]
        if cols:
            out[name] = table_num[cols].mean(axis=1)
    return out


def main():
    cfg = load_config()
    data_dir = cfg["data_dir"]
    out_dir = data_dir / "report"
    out_dir.mkdir(exist_ok=True)
    log_path = data_dir / "schedule_log.csv"
    if not log_path.exists():
        print(f"出勤の記録が見つかりません: {log_path}\n先にタスク2（schedule_logger.py）で記録をためてください。")
        sys.exit(1)

    hours = list(range(int(cfg["hour_range"][0]), int(cfg["hour_range"][1])))
    df = load_schedule(log_path, cfg)
    if df.empty:
        print("集計できる出勤記録がありません（今日以前の記録がまだない可能性）。")
        sys.exit(1)
    daily = headcount_by_hour(df, hours)
    n_days = len(daily)
    weeks = n_days / 7
    first, last = min(daily["対象日"]), max(daily["対象日"])

    # 曜日×時間帯の表
    table_num = daily.groupby("曜日")[hours].mean().reindex(range(7))
    table_num.index = [WEEKDAYS[i] for i in table_num.index]
    # 一度も出勤がない時間（営業時間外）は表から外す
    active = [h for h in hours if (table_num[h].fillna(0) > 0).any()]
    table_num = table_num[active]
    table = table_num.copy()
    table.columns = [hour_label(h) for h in active]
    samples = daily.groupby("曜日").size().reindex(range(7)).fillna(0).astype(int)
    out_table = table.round(2)
    out_table.insert(0, "データ日数", samples.values)
    out_table.to_csv(out_dir / "weekday_hour_table.csv", encoding="utf-8-sig")
    plot_heatmap(table, out_dir / "heatmap_weekday_hour.png", n_days)

    day_avg = daily.groupby("曜日")["1日の出勤人数"].mean().reindex(range(7))
    bands = band_table(table_num, active)
    stacked = bands.stack().dropna()
    stacked = stacked[stacked > 0.3]  # ほぼ営業していない時間帯は除く
    thick = stacked.sort_values(ascending=False).head(3)
    thin = stacked.sort_values().head(3)

    trend = load_trend_months(data_dir / "trends_month_rank.csv")
    soldout = load_soldout(data_dir / "availability_log.csv", set(daily["対象日"]))
    hourly = load_hourly_search(data_dir / "trends_hourly_log.csv")

    # ------------------------------------------------ レポート作成
    L = []
    L.append("# 繁忙期・繁忙曜日の分析（推定）")
    L.append("")
    L.append(f"作成日: {datetime.now():%Y年%m月%d日}　／　出勤データの期間: {first:%Y/%m/%d}〜{last:%Y/%m/%d}（{n_days}日分・約{weeks:.1f}週）")
    L.append("")
    L.append("> **この資料はすべて「推定」です。** 出勤人数は「お店が混みそうだと見込んで入れた人数」であり、"
             "実際の来店数ではありません。Googleの検索量も「関心の高さの目安」で、来店数そのものではありません。")
    L.append("")
    if n_days < 28:
        L.append(f"> ⚠ **データがまだ少ない（{n_days}日分・4週間未満）ため、以下の傾向はまだ確かではありません。**"
                 "各曜日のデータが数日分しかなく、たまたまの増減に左右されます。4週間以上たまったら、もう一度実行してください。")
        L.append("")
    few = [WEEKDAYS[i] for i in range(7) if samples[i] < 4]
    if few:
        L.append(f"> ※ {'・'.join(few)}曜日はデータが4日分未満です。")
        L.append("")

    # 1. 月
    L.append("## 1. 忙しいと推定される月")
    L.append("")
    if trend:
        avg, kws = trend
        top = list(avg.index[:3])
        low = list(avg.index[-3:][::-1])
        L.append(f"Googleで「{'」「'.join(kws)}」が検索された量（過去5年の月ごとの平均）から推定しました。")
        L.append("")
        L.append(f"- **検索が多い月（忙しくなりやすいと推定）: {'、'.join(f'{m}月' for m in top)}**")
        L.append(f"- 検索が少ない月（落ち着きやすいと推定）: {'、'.join(f'{m}月' for m in low)}")
        L.append("")
        L.append("| 月 | 1年の平均と比べて |")
        L.append("|---|---|")
        for m in range(1, 13):
            if m in avg.index:
                v = avg[m]
                L.append(f"| {m}月 | {'+' if v >= 0 else ''}{v:.0f}% |")
        L.append("")
        L.append("グラフ: `~/esthe_data/trends_monthly.png`")
    else:
        top = []
        L.append("Googleトレンドのデータがまだありません。`python trends.py` を実行すると、この欄が埋まります。")
    L.append("")

    # 2. 曜日・時間帯
    L.append("## 2. 出勤が厚い・薄い曜日と時間帯")
    L.append("")
    L.append("### 曜日ごとの1日の平均出勤人数")
    L.append("")
    L.append("| 曜日 | 平均出勤人数 | データ日数 |")
    L.append("|---|---|---|")
    for i in range(7):
        v = day_avg[i]
        L.append(f"| {WEEKDAYS[i]} | {'-' if pd.isna(v) else f'{v:.1f}人'} | {samples[i]}日 |")
    L.append("")
    ranked = day_avg.dropna().sort_values(ascending=False)
    if len(ranked) >= 2:
        L.append(f"- 出勤が**多い**曜日: {'、'.join(WEEKDAYS[i] + '曜' for i in ranked.index[:2])}")
        L.append(f"- 出勤が**少ない**曜日: {'、'.join(WEEKDAYS[i] + '曜' for i in ranked.index[-2:][::-1])}")
        L.append("")
    L.append("### 時間帯（曜日×時間帯の平均出勤人数）")
    L.append("")
    L.append("- 出勤が**厚い**ところ: " + "、".join(f"{d}曜の{b}（平均{v:.1f}人）" for (d, b), v in thick.items()))
    L.append("- 出勤が**薄い**ところ: " + "、".join(f"{d}曜の{b}（平均{v:.1f}人）" for (d, b), v in thin.items()))
    L.append("")
    L.append("色の表（濃いほど出勤人数が多い）: `~/esthe_data/report/heatmap_weekday_hour.png`  ")
    L.append("数字の表: `~/esthe_data/report/weekday_hour_table.csv`")
    L.append("")

    extra_hits = []
    if soldout is not None:
        L.append("### 予約が埋まった（受付終了など）表示の数")
        L.append("")
        L.append("公式サイトに「受付終了」などと出ていたキャストの、1日あたりの平均人数です。"
                 "**ここが多い曜日は、出勤人数より来店希望のほうが多かった可能性があります。**")
        L.append("")
        L.append("| 曜日 | 1日あたりの受付終了の人数 |")
        L.append("|---|---|")
        for i in range(7):
            v = soldout.get(i, np.nan)
            L.append(f"| {WEEKDAYS[i]} | {'-' if pd.isna(v) else f'{v:.1f}人'} |")
        L.append("")
        so = soldout.dropna().sort_values(ascending=False)
        so = so[so > 0]
        if not so.empty:
            extra_hits.append("予約が埋まりやすい曜日（" + "、".join(WEEKDAYS[i] + "曜" for i in so.index[:2]) + "）")

    if hourly is not None:
        search, sdays = hourly
        L.append("### 検索が多い時間帯との比較（参考）")
        L.append("")
        # 1週間の中での割合どうしで比べる（単位が違うため）
        st = search.unstack().reindex(index=range(7))
        cols = [h for h in active if h in st.columns]
        if cols:
            s_share = st[cols] / np.nansum(st[cols].values)
            c_share = table_num[cols].copy()
            c_share.index = range(7)
            c_share = c_share / np.nansum(c_share.values)
            gap = (s_share - c_share).stack().sort_values(ascending=False)
            gap = gap[gap > 0].head(3)
            if not gap.empty:
                desc = "、".join(f"{WEEKDAYS[d]}曜の{hour_label(h)}台" for (d, h), _ in gap.items())
                L.append(f"Googleでの検索の多さ（{sdays}日分）と比べて、**検索の割に出勤が少ない**時間: {desc}")
                extra_hits.append(f"検索の割に出勤が少ない時間（{desc}）")
            else:
                L.append("検索の多さと出勤の厚さに、目立ったずれはありませんでした。")
            L.append("")
            if sdays < 28:
                L.append(f"※ 時間別の検索データは{sdays}日分と少ないため、あくまで参考です。")
                L.append("")

    # 3. 提案
    L.append("## 3. キャスト増員を検討すべき時期の案（推定）")
    L.append("")
    props = []
    if top:
        prep = sorted({(m - 2) % 12 + 1 for m in top} - set(top))
        props.append(f"**{'・'.join(f'{m}月' for m in top)}** は検索が多く、忙しくなると推定されます。"
                 f"この時期に向けて、前月（{'・'.join(f'{m}月' for m in prep) or '直前の月'}）までに出勤を増やせるよう準備するのがおすすめです。")
    else:
        props.append("月ごとの判断には Googleトレンドのデータが必要です（`python trends.py`）。")
    if len(ranked) >= 2:
        props.append(f"普段から出勤を厚くしている **{'・'.join(WEEKDAYS[i] + '曜' for i in ranked.index[:2])}** と、"
                 f"**{'、'.join(f'{d}曜の{b}' for (d, b) in list(thick.index)[:2])}** は、お店が「混む」と見込んでいる時間です。"
                 "忙しい月には、ここをさらに1〜2人増やすことを検討してください。")
    if extra_hits:
        props.append(f"{'、'.join(extra_hits)} は、人手が足りていない可能性があるため優先的に増員を検討する候補です。")
    else:
        props.append("出勤が薄い時間帯（上の「薄いところ」）は、お客様が少ないから薄いのか、人が足りないから薄いのかが、"
                 "このデータだけでは分かりません。試しに1人増やして、予約の入り方が変わるかを数週間見てみるのがおすすめです。")
    L += [f"{i}. {p}" for i, p in enumerate(props, 1)]
    L.append("")
    L.append("---")
    L.append("※ この資料は出勤人数と検索量から推定したもので、実際の来店数を表すものではありません。"
             "実際に増員したときの予約数・売上と見比べて、少しずつ判断を調整してください。")
    if n_days < 28:
        L.append(f"※ データが{n_days}日分（4週間未満）のため、結論はまだ仮のものです。")

    report = "\n".join(L) + "\n"
    (out_dir / "report.md").write_text(report, encoding="utf-8")
    print(report)
    print(f"保存しました:\n  {out_dir / 'report.md'}\n  {out_dir / 'heatmap_weekday_hour.png'}\n  {out_dir / 'weekday_hour_table.csv'}")


if __name__ == "__main__":
    main()
