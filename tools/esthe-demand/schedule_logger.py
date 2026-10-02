"""タスク2・3：公式サイトの出勤スケジュールを1日1回記録する。

使い方:
  python schedule_logger.py --check   # 動作確認（robots.txt・利用規約を確認し、読み取り結果を表示。CSVには書かない）
  python schedule_logger.py           # 本番（CSVに追記。1日1回まで）

保存先（~/esthe_data/）:
  schedule_log.csv       記録日時, 対象日, キャスト名, 開始, 終了
  availability_log.csv   （空き状況が公開されている場合のみ）記録日時, 対象日, キャスト名, 状態
  run_log.txt            実行の記録（自動実行の結果確認用）
  debug/                 確認モードで保存したページ（読み取りがうまくいかないときの調査用）

サイトに負荷をかけないための決まり:
  - 本番の実行は1日1回まで（2回目は何もせず終了）
  - ページの取得は1回の実行で最大 9 ページ程度、間隔は3秒以上（robots.txt の指定があればそれ以上）
  - robots.txt で禁止されている、またはアクセスを拒否された（403/429など）場合はすぐに中止し、回避はしない
"""
import argparse
import csv
import hashlib
import json
import re
import sys
import time
from datetime import date, datetime, timedelta
from urllib.parse import urljoin, urlparse
from urllib import robotparser

import requests
from bs4 import BeautifulSoup

from common import load_config

# ---------------------------------------------------------------- 正規表現
SEP = r"\s*[~〜～\-－ー–—]\s*"
TIME_RE = re.compile(
    r"(\d{1,2})\s*[:：]\s*(\d{2})" + SEP +
    r"(翌\s*)?(?:(\d{1,2})\s*[:：]\s*(\d{2})|(LAST|Last|last|ラスト|L\.?O\.?))"
)
DATE_TEXT_RE = re.compile(r"(?:(20\d{2})[/年.\-])?(\d{1,2})\s*[/月.]\s*(\d{1,2})\s*日?")
DATE_URL_RES = [
    re.compile(r"(20\d{2})[-/_]?(\d{2})[-/_]?(\d{2})"),
]
STATUS_WORDS = ["受付終了", "完売", "満了", "満枠", "予約満了", "残りわずか", "残り僅か", "空きあり",
                "空き有", "予約可", "案内可能", "即案内", "待機中", "ご案内中", "接客中", "TEL確認", "要問合せ", "要確認"]
STRONG_TERMS = ["スクレイピング", "クローリング", "クローラ", "クローラー", "ロボット", "自動取得",
                "自動的に取得", "機械的", "自動化ツール", "bot", "BOT"]
WEAK_TERMS = ["無断転載", "無断複製", "転載", "複製"]
TERMS_LINK_WORDS = ["利用規約", "規約", "ご利用にあたって", "注意事項", "禁止事項", "免責", "サイトポリシー", "terms"]

BLOCK_STATUS = {401, 403, 405, 406, 429, 451, 503}


class StopRun(Exception):
    """安全のため実行を止めるときに使う。"""


# ---------------------------------------------------------------- 状態・ログ
class State:
    def __init__(self, path):
        self.path = path
        try:
            self.data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            self.data = {}

    def save(self):
        self.path.write_text(json.dumps(self.data, ensure_ascii=False, indent=2), encoding="utf-8")


def log(cfg, msg):
    line = f"[{datetime.now():%Y-%m-%d %H:%M:%S}] {msg}"
    print(line)
    with open(cfg["data_dir"] / "run_log.txt", "a", encoding="utf-8") as f:
        f.write(line + "\n")


# ---------------------------------------------------------------- 通信
class PoliteClient:
    def __init__(self, cfg):
        self.cfg = cfg
        self.s = requests.Session()
        self.s.headers.update({"User-Agent": cfg["user_agent"], "Accept-Language": "ja,en;q=0.5"})
        self.interval = float(cfg["request_interval_sec"])
        self.last = 0.0
        self.count = 0
        self.robots = None

    def _wait(self):
        gap = time.time() - self.last
        if self.last and gap < self.interval:
            time.sleep(self.interval - gap)
        self.last = time.time()

    def load_robots(self, site):
        url = urljoin(site, "/robots.txt")
        self._wait()
        self.count += 1
        r = self.s.get(url, timeout=20)
        rp = robotparser.RobotFileParser()
        if r.status_code in (404, 410):
            rp.parse([])
            note = "robots.txt はありません（制限なしとして扱います）"
        elif r.status_code in (401, 403):
            raise StopRun(f"robots.txt へのアクセスが拒否されました（{r.status_code}）。サイト全体が自動アクセス禁止の可能性があるため中止します。")
        elif r.status_code >= 500:
            raise StopRun(f"robots.txt が取得できません（サーバーエラー {r.status_code}）。今日は中止します。")
        else:
            r.encoding = r.encoding or "utf-8"
            rp.parse(r.text.splitlines())
            note = "robots.txt を確認しました"
        self.robots = rp
        delay = rp.crawl_delay(self.cfg["user_agent"]) or rp.crawl_delay("*")
        if delay:
            self.interval = max(self.interval, float(delay))
            note += f"（指定された待ち時間 {delay} 秒を守ります）"
        return note, (r.text if r.status_code == 200 else "")

    def allowed(self, url):
        ua = self.cfg["user_agent"]
        return self.robots.can_fetch(ua, url) and self.robots.can_fetch("*", url)

    def get(self, url):
        if not self.allowed(url):
            raise StopRun(f"robots.txt で禁止されているページです: {url}")
        if self.count >= 12:
            raise StopRun("1回の実行でのアクセス上限（12回）に達したため止めます。")
        self._wait()
        self.count += 1
        r = self.s.get(url, timeout=30)
        if r.status_code in BLOCK_STATUS:
            raise StopRun(f"サイトからアクセスを断られました（{r.status_code}）: {url}\n"
                          "ボット対策などでブロックされている可能性があります。回避はせず、ここで止めます。")
        r.raise_for_status()
        if not r.encoding or r.encoding.lower() == "iso-8859-1":
            r.encoding = r.apparent_encoding
        return r.text


# ---------------------------------------------------------------- 利用規約の確認
def check_terms(client, cfg, state, ack):
    site = cfg["site"]
    if not client.allowed(site):
        return ["トップページは robots.txt で取得対象外のため、利用規約は自動確認していません。ブラウザで確認してください。"], False
    try:
        top = client.get(site)
    except StopRun:
        raise
    except Exception as e:
        return [f"トップページを開けず、利用規約を確認できませんでした: {e}"], False
    soup = BeautifulSoup(top, "html.parser")
    links = []
    for a in soup.find_all("a", href=True):
        text = a.get_text(" ", strip=True)
        href = urljoin(site, a["href"])
        if urlparse(href).netloc != urlparse(site).netloc:
            continue
        if any(w.lower() in (text + " " + a["href"]).lower() for w in TERMS_LINK_WORDS):
            if href not in links:
                links.append(href)
    notes = []
    if not links:
        notes.append("利用規約らしいページへのリンクは見つかりませんでした（禁止事項は確認できず）。")
        return notes, False

    strong_hits, weak_hits = [], []
    for url in links[:2]:
        if not client.allowed(url):
            notes.append(f"規約ページは robots.txt で取得対象外のため、ブラウザで確認してください: {url}")
            continue
        try:
            text = BeautifulSoup(client.get(url), "html.parser").get_text(" ", strip=True)
        except StopRun:
            raise
        except Exception as e:
            notes.append(f"規約ページを開けませんでした: {url}（{e}）")
            continue
        notes.append(f"規約ページを確認: {url}")
        for w in STRONG_TERMS:
            for m in re.finditer(re.escape(w), text):
                strong_hits.append((url, text[max(0, m.start() - 60): m.end() + 60]))
        for w in WEAK_TERMS:
            for m in re.finditer(re.escape(w), text):
                weak_hits.append((url, text[max(0, m.start() - 40): m.end() + 40]))

    if weak_hits:
        notes.append("「転載・複製」に関する決まりがあります（集計結果を外部に公開しなければ通常は問題ありません）:")
        for ctx in list(dict.fromkeys(c for _, c in weak_hits))[:3]:
            notes.append(f"    …{ctx}…")

    if strong_hits:
        digest = hashlib.sha256("".join(c for _, c in strong_hits).encode()).hexdigest()[:16]
        if ack:
            state.data["terms_ack"] = digest
            notes.append("自動取得に関する記述を確認済みとして記録しました（--ack-terms）。")
            return notes, False
        if state.data.get("terms_ack") == digest:
            notes.append("自動取得に関する記述がありますが、確認済み（内容に変更なし）のため続行します。")
            return notes, False
        notes.append("！ 自動取得（ロボット・スクレイピング等）に関する記述が見つかりました:")
        for u, ctx in strong_hits[:5]:
            notes.append(f"    {u}\n    …{ctx}…")
        return notes, True
    notes.append("自動取得を禁止する記述は見つかりませんでした。")
    return notes, False


# ---------------------------------------------------------------- 日付
def infer_date(month, day, today, year=None):
    if year:
        try:
            return date(int(year), int(month), int(day))
        except ValueError:
            return None
    best = None
    for y in (today.year - 1, today.year, today.year + 1):
        try:
            d = date(y, int(month), int(day))
        except ValueError:
            continue
        if best is None or abs((d - today).days) < abs((best - today).days):
            best = d
    return best


def date_from_url(href, today):
    for rx in DATE_URL_RES:
        m = rx.search(href)
        if m:
            try:
                return date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
            except ValueError:
                pass
    m = re.search(r"[?&](?:date|day|d|ymd)=(\d{1,2})[-/]?(\d{1,2})(?:&|$)", href)
    if m:
        return infer_date(m.group(1), m.group(2), today)
    return None


def find_date_links(html, base_url, today, days):
    soup = BeautifulSoup(html, "html.parser")
    host = urlparse(base_url).netloc
    found = {}
    for a in soup.find_all("a", href=True):
        href = urljoin(base_url, a["href"])
        u = urlparse(href)
        if u.netloc != host or "schedule" not in u.path.lower():
            continue
        d = date_from_url(href, today)
        if d and today <= d < today + timedelta(days=days) and d not in found:
            found[d] = href
    return found


# ---------------------------------------------------------------- 出勤カードの読み取り
def normalize_times(m):
    sh, sm, next_day, eh, em, last = m.groups()
    start = int(sh) * 60 + int(sm)
    start_s = f"{int(sh)}:{sm}"
    if last:
        return start_s, "LAST"
    end = int(eh) * 60 + int(em)
    if next_day or end <= start:
        end += 24 * 60
    return start_s, f"{end // 60}:{end % 60:02d}"


def clean_name(text):
    t = re.sub(r"[\(（]\s*\d{2}\s*(歳)?\s*[\)）]", "", text)
    t = re.sub(r"\d{2}\s*歳.*$", "", t)
    t = re.sub(r"\s*(T|身長)\s*\d{3}.*$", "", t)
    t = re.sub(r"(NEW|New|new|新人|体験入店|本日出勤)", "", t)
    t = t.strip(" 　/|・-：:")
    return t[:30]


def guess_name(card):
    for el in card.find_all(True):
        cls = " ".join(el.get("class", [])) + " " + (el.get("id") or "")
        if re.search(r"name", cls, re.I):
            n = clean_name(el.get_text(" ", strip=True))
            if n and not TIME_RE.search(n):
                return n
    for img in card.find_all("img"):
        alt = clean_name(img.get("alt") or "")
        if alt and len(alt) <= 20 and not re.search(r"(logo|icon|new|バナー|画像)", alt, re.I):
            return alt
    for line in card.get_text("\n", strip=True).split("\n"):
        if TIME_RE.search(line) or DATE_TEXT_RE.fullmatch(line.strip()) or any(w in line for w in STATUS_WORDS):
            continue
        n = clean_name(line)
        if 1 <= len(n) <= 15 and not re.fullmatch(r"[\d\s\W]+", n):
            return n
    return None


def find_status(card):
    text = card.get_text(" ", strip=True)
    for w in STATUS_WORDS:
        if w in text:
            return w
    return None


def cards_by_selector(soup, sel):
    out = []
    for card in soup.select(sel["card"]):
        t_el = card.select_one(sel["time"]) if sel.get("time") else card
        n_el = card.select_one(sel["name"]) if sel.get("name") else None
        m = TIME_RE.search(t_el.get_text(" ", strip=True)) if t_el else None
        if not m:
            continue
        name = clean_name(n_el.get_text(" ", strip=True)) if n_el else guess_name(card)
        out.append((card, name, m))
    return out


def cards_by_heuristic(soup):
    """時刻（12:00〜24:00 など）が1つだけ入っている、いちばん大きな枠を「1人分のカード」とみなす。"""
    out, seen = [], set()
    for node in soup.find_all(string=TIME_RE):
        el = node.parent
        card = el
        cur = el
        for _ in range(8):
            parent = cur.parent
            if parent is None or parent.name in ("body", "html", "[document]"):
                break
            if len(TIME_RE.findall(parent.get_text(" ", strip=True))) > 1:
                break
            cur = parent
            card = cur
        if id(card) in seen:
            continue
        seen.add(id(card))
        m = TIME_RE.search(card.get_text(" ", strip=True))
        if m:
            out.append((card, guess_name(card), m))
    return out


def extract_cards(soup, cfg):
    sel = cfg.get("selectors") or {}
    if sel.get("card"):
        return cards_by_selector(soup, sel)
    return cards_by_heuristic(soup)


def date_headings_before(soup, today):
    """ページ内の「10/2(木)」のような日付見出しを、文書内の順番つきで返す。"""
    order = {id(el): i for i, el in enumerate(soup.find_all(True))}
    heads = []
    for node in soup.find_all(string=DATE_TEXT_RE):
        text = node.strip()
        if len(text) > 20 or TIME_RE.search(text):
            continue
        m = DATE_TEXT_RE.search(text)
        d = infer_date(m.group(2), m.group(3), today, m.group(1))
        if d:
            heads.append((order.get(id(node.parent), -1), d))
    return sorted(heads), order


def parse_page(html, cfg, today, page_date=None):
    """1ページを読み取り、(対象日, 名前, 開始, 終了, 状態) のリストと注意メッセージを返す。"""
    soup = BeautifulSoup(html, "html.parser")
    for t in soup(["script", "style", "noscript"]):
        t.decompose()
    cards = extract_cards(soup, cfg)
    notes = []
    rows = []
    if page_date is None:
        heads, order = date_headings_before(soup, today)
        # 日付見出しが連続して並ぶだけ（タブ）だと割り当てられないので注意を出す
        if heads:
            positions = [order.get(id(c), -1) for c, _, _ in cards]
            first_card = min(positions) if positions else 10 ** 9
            before = [h for h in heads if h[0] < first_card]
            if len(before) >= 3 and len(before) == len(heads):
                notes.append("日付がタブ形式で、カードと日付の対応が自動で判断できません。"
                             "当日分として扱います（README の「読み取りがうまくいかないとき」を参照）。")
                heads = []
    for card, name, m in cards:
        d = page_date
        if d is None:
            pos = order.get(id(card), -1)
            prior = [hd for p, hd in heads if p <= pos]
            d = prior[-1] if prior else today
        start, end = normalize_times(m)
        rows.append((d, name, start, end, find_status(card)))
    if page_date is None and not heads and cards:
        notes.append("ページ内に日付見出しが見つからないため、すべて「今日」の出勤として扱いました。")
    # 同じ日・同じ人の重複は1つにまとめる
    uniq, keys = [], set()
    for r in rows:
        k = (r[0], r[1], r[2])
        if k in keys:
            continue
        keys.add(k)
        uniq.append(r)
    return uniq, notes


# ---------------------------------------------------------------- 実行
def collect(client, cfg, today, debug_dir=None):
    base = cfg["schedule_url"]
    days = int(cfg["days"])
    html = client.get(base)
    if debug_dir:
        (debug_dir / "schedule_top.html").write_text(html, encoding="utf-8")
    links = find_date_links(html, base, today, days)
    notes = []
    rows = []
    if len(links) >= 2:
        notes.append(f"日付ごとのページを見つけました（{len(links)}日分）。順番に取得します。")
        fetched_today = False
        for d in sorted(links):
            url = links[d]
            page = client.get(url)
            if debug_dir:
                (debug_dir / f"schedule_{d:%Y%m%d}.html").write_text(page, encoding="utf-8")
            r, n = parse_page(page, cfg, today, page_date=d)
            rows += r
            notes += n
            fetched_today = fetched_today or d == today
        if not fetched_today:
            r, n = parse_page(html, cfg, today, page_date=today)
            rows += r
    else:
        r, n = parse_page(html, cfg, today)
        rows += r
        notes += n
    rows = [r for r in rows if today <= r[0] < today + timedelta(days=days)]
    return rows, notes


def append_csv(path, header, rows):
    is_new = not path.exists()
    # 新規作成のときだけ BOM 付き（Excel で文字化けしない）。追記時に BOM を重ねない。
    with open(path, "a", newline="", encoding="utf-8-sig" if is_new else "utf-8") as f:
        w = csv.writer(f)
        if is_new:
            w.writerow(header)
        w.writerows(rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="動作確認（CSVには書き込まない）")
    ap.add_argument("--ack-terms", action="store_true", help="利用規約の該当箇所を読んで問題ないと確認した場合のみ使う")
    args = ap.parse_args()

    cfg = load_config()
    data_dir = cfg["data_dir"]
    state = State(data_dir / ".logger_state.json")
    today = date.today()
    now = datetime.now()

    # ---- 1日1回まで
    if not args.check and state.data.get("last_run") == today.isoformat():
        log(cfg, "今日はすでに記録済みのため、何もせず終了します（1日1回まで）。")
        return 0
    if args.check:
        key = f"checks_{today.isoformat()}"
        n = state.data.get(key, 0)
        if n >= 3:
            print("確認モードは1日3回までにしています（サイトへの負荷を避けるため）。明日もう一度試してください。")
            return 1
        state.data = {k: v for k, v in state.data.items() if not k.startswith("checks_")}
        state.data[key] = n + 1
        state.save()

    client = PoliteClient(cfg)
    try:
        # ---- robots.txt
        note, robots_text = client.load_robots(cfg["site"])
        log(cfg, note)
        if args.check and robots_text:
            print("---- robots.txt の内容 ----\n" + robots_text.strip()[:1500] + "\n--------------------------")
        if not client.allowed(cfg["schedule_url"]):
            raise StopRun(f"robots.txt で出勤ページ（{cfg['schedule_url']}）の自動取得が禁止されています。")
        log(cfg, "robots.txt：出勤ページの取得は禁止されていません。")

        # ---- 利用規約
        notes, stop = check_terms(client, cfg, state, args.ack_terms)
        for n in notes:
            log(cfg, n)
        state.save()
        if stop:
            raise StopRun("利用規約に自動取得を制限する可能性のある記述があるため止めました。\n"
                          "上の文章を読み、問題がない（例：自社サイトで管理者の了承がある）と判断できた場合のみ、\n"
                          "  python schedule_logger.py --check --ack-terms\n"
                          "を実行してください。規約の文章が変わると、再びここで止まります。")

        # ---- 出勤スケジュール
        debug_dir = None
        if args.check:
            debug_dir = data_dir / "debug"
            debug_dir.mkdir(exist_ok=True)
        rows, notes = collect(client, cfg, today, debug_dir)
        for n in notes:
            log(cfg, n)
    except StopRun as e:
        log(cfg, f"中止: {e}")
        return 2
    except requests.exceptions.SSLError as e:
        log(cfg, f"中止: SSL（暗号化通信）のエラーです。README の「SSLエラーが出たとき」を見てください。\n  {e}")
        return 3
    except requests.exceptions.RequestException as e:
        log(cfg, f"中止: 通信エラー {e}")
        return 3

    named = [r for r in rows if r[1]]
    unnamed = len(rows) - len(named)
    stamp = now.strftime("%Y-%m-%d %H:%M")
    per_day = {}
    for r in named:
        per_day[r[0]] = per_day.get(r[0], 0) + 1

    if args.check:
        print(f"\n========== 読み取り結果（{len(named)}件・CSVには書き込んでいません） ==========")
        for d in sorted(per_day):
            print(f"  {d:%m/%d}({'月火水木金土日'[d.weekday()]}) {per_day[d]}人")
        print("\n  先頭10件（名前・時刻が正しく読めているか確認してください）:")
        for r in named[:10]:
            print(f"   {r[0]}  {r[1]}  {r[2]} 〜 {r[3]}" + (f"  [{r[4]}]" if r[4] else ""))
        if unnamed:
            print(f"\n  ※ 名前が読み取れなかった出勤が {unnamed} 件ありました（記録から除外）。")
        statuses = [r for r in named if r[4]]
        if statuses:
            print(f"\n  空き状況らしい表示が {len(statuses)} 件ありました（タスク3の記録対象）。")
        else:
            print("\n  空き状況の表示は見つかりませんでした → タスク3（空き状況の記録）は対象外です。")
        if not named:
            print("\n  出勤情報を読み取れませんでした。README の「読み取りがうまくいかないとき」を見てください。")
        print(f"\n  調査用に取得したページを保存しました: {data_dir / 'debug'}")
        return 0 if named else 4

    if not named:
        log(cfg, "出勤情報を1件も読み取れませんでした（サイトの作りが変わった可能性）。CSVには何も追記していません。")
        return 4

    append_csv(data_dir / "schedule_log.csv", ["記録日時", "対象日", "キャスト名", "開始", "終了"],
               [[stamp, r[0].isoformat(), r[1], r[2], r[3]] for r in named])
    if cfg.get("record_availability"):
        st = [[stamp, r[0].isoformat(), r[1], r[4]] for r in named if r[4]]
        if st:
            append_csv(data_dir / "availability_log.csv", ["記録日時", "対象日", "キャスト名", "状態"], st)
    state.data["last_run"] = today.isoformat()
    state.save()
    summary = "、".join(f"{d:%m/%d}:{n}人" for d, n in sorted(per_day.items()))
    log(cfg, f"記録しました: {len(named)}件（{summary}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
