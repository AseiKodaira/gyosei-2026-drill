# メンズエステ 繁忙期・繁忙曜日の分析ツール

TOKYO LUXURY（上野・御徒町）向け。外部データから「いつ忙しくなりそうか」を**推定**し、キャストを増やす時期の検討に使います。

| ファイル | 役割 |
|---|---|
| `trends.py` | タスク1：Googleトレンドで月ごとの検索量を調べ、順位とグラフを作る |
| `schedule_logger.py` | タスク2・3：公式サイトの出勤スケジュール（と、公開されていれば空き状況）を1日1回記録する |
| `analyze.py` | タスク4：たまった記録を集計し、日本語のまとめを作る |
| `setup.sh` | 最初に1回だけ。専用の Python 環境を作る |
| `install_launchd.sh` / `uninstall_launchd.sh` | 毎日の自動実行の開始／停止 |
| `config.json` | 設定（キーワード、閉店時刻など） |

データはすべて `~/esthe_data/` に保存されます。

---

## 0. 準備（最初に1回だけ）

ターミナル（「アプリケーション」→「ユーティリティ」→「ターミナル」）を開き、以下を**1ブロックずつ**コピーして貼り付け、Enter を押してください。

### 0-1. ツールをダウンロードして `~/esthe_tool` に置く

```bash
cd ~
git clone --depth 1 -b claude/mens-esthe-demand-analysis-pdxjf7 https://github.com/aseikodaira/gyosei-2026-drill.git esthe_repo
cp -R esthe_repo/tools/esthe-demand ~/esthe_tool
cd ~/esthe_tool && ls
```

> `git` のインストールを求める画面が出たら「インストール」を押し、終わったらもう一度貼り付けてください。
> GitHub のパスワードを聞かれた場合は、ブラウザで GitHub にログインしてリポジトリのページからブランチ `claude/mens-esthe-demand-analysis-pdxjf7` の ZIP をダウンロードし、中の `tools/esthe-demand` フォルダを `~/esthe_tool` という名前でホームフォルダに置いてください。
>
> ※「書類」「デスクトップ」「ダウンロード」フォルダの中には置かないでください（Mac の保護機能で自動実行が止められることがあります）。

### 0-2. 専用の Python 環境を作る（SSL 警告対策込み）

```bash
cd ~/esthe_tool
bash setup.sh
```

最後に `✅ 準備ができました` と `通信テスト: 200 OK` が出れば成功です。

> Mac 標準の Python 3.9.6 は「LibreSSL」という暗号化の部品を使っていて、新しい `urllib3`（2.x）と組み合わせると警告や通信失敗が起きます。
> `setup.sh` は urllib3 を 1.x 系に、requests をそれに合う版に固定して入れるので、この問題は起きません。

以降、コマンドはすべて次の形で実行します（`.venv/bin/python` が専用環境の Python です）。

---

## 1. タスク1：Googleトレンド（月ごとの波）

```bash
cd ~/esthe_tool
.venv/bin/python trends.py
```

- 成功すると、月別ランキングが画面に表示され、次のファイルができます。
  - `~/esthe_data/trends_monthly.png`（グラフ）… `open ~/esthe_data/trends_monthly.png` で開けます
  - `~/esthe_data/trends_month_rank.csv`（月別の平均と順位）
- 「TOKYO LUXURY」のように検索がほとんどない語は、数字がばらつくため自動で集計から外します（その旨が表示されます）。

### 自動取得に失敗したとき（よくあります）

Google は自動の取得を制限することがあり、「429」などのエラーで失敗する場合があります。そのときは画面に**CSVの置き方**が表示されます。手順は次のとおりです。

1. ブラウザで https://trends.google.co.jp/trends/explore?geo=JP&date=today%205-y を開く
2. 「上野 メンズエステ」「御徒町 メンズエステ」「TOKYO LUXURY」を入力（「比較を追加」で並べる）
3. グラフ右上の ↓ ボタンで CSV を保存
4. 次を貼り付けて、CSV を所定のフォルダに移してから再実行

```bash
mkdir -p ~/esthe_data/trends_csv
mv ~/Downloads/multiTimeline*.csv ~/esthe_data/trends_csv/
cd ~/esthe_tool && .venv/bin/python trends.py --csv
```

---

## 2. タスク2：出勤スケジュールの記録

### 2-1. 動作確認（まずはこれ）

```bash
cd ~/esthe_tool
.venv/bin/python schedule_logger.py --check
```

このコマンドは次の順に動き、**CSV には書き込みません**。

1. `robots.txt`（サイトが自動アクセスについて定めたファイル）を読み、出勤ページの取得が禁止されていれば**その場で止めて報告**
2. トップページから「利用規約」などのページを探して読み、「スクレイピング」「ロボット」「自動取得」などの禁止を示す言葉があれば**止めて該当箇所を表示**
3. 出勤ページを読み、日付ごとの人数と先頭10件（日付・名前・開始・終了）を表示
4. 空き状況（「受付終了」など）の表示があるかを報告（タスク3の判定）

表示された名前・時刻がサイトと合っているか、ブラウザで https://tokyo-luxury.jp/schedule/ を開いて見比べてください。

- 確認モードはサイトの負担を考えて**1日3回まで**にしています。
- `中止:` と表示された場合は、その理由が書いてあります。**無理に回避せず**、内容をそのまま共有してください。
- 利用規約で止まった場合、該当箇所を読んで「問題ない（例：自店のサイトで、サイト管理者の了承がある）」と判断できたときだけ `--check --ack-terms` を付けて実行すると先に進めます。規約の文章が変わると再び止まります。

### 2-2. 本番の記録（1回だけ手で試す）

```bash
cd ~/esthe_tool
.venv/bin/python schedule_logger.py
cat ~/esthe_data/schedule_log.csv | head
```

- `~/esthe_data/schedule_log.csv` に `記録日時,対象日,キャスト名,開始,終了` が追記されます（Excel でそのまま開けます）。
- **1日1回まで**しか記録しません。同じ日に2回目を実行しても「すでに記録済み」と出て何もしません。
- 1回の実行でのアクセスは robots.txt・トップ・規約・出勤ページ（最大で日付ごと7ページ）程度で、各アクセスの間は3秒以上あけます。
- 終了時刻が「翌3:00」のような日またぎは `27:00` のように記録します。「LAST」はそのまま記録し、集計のときに `config.json` の `last_time`（初期値 `29:00` ＝ 翌5時）として扱います。**実際の閉店時刻に合わせて書き換えてください。**

### 読み取りがうまくいかないとき

`--check` で人数が0人だったり、名前がおかしかったりする場合は、サイトの作りに合わせた調整が必要です。
`~/esthe_data/debug/` に保存されたページ（`schedule_top.html` など）を共有してもらえれば、読み取り方法を調整します。
（`config.json` の `selectors` に、カード・名前・時刻の場所を指定できるようにしてあります。）

---

## 3. タスク3：空き状況（任意）

`--check` の最後に次のどちらかが表示されます。

- `空き状況らしい表示が N 件ありました` → 毎日の記録のときに `~/esthe_data/availability_log.csv` にも自動で記録します（追加の設定は不要）。
- `空き状況の表示は見つかりませんでした` → 公式サイトで空き状況が公開されていないため、**タスク3は対象外**です。

ログインが必要な予約ページや、ボット対策のあるポータルサイトは対象にしていません。アクセスを拒否された場合（403・429 など）は、その場で止まり、回避はしません。

---

## 4. 自動実行の設定（2 が動いたら）

```bash
cd ~/esthe_tool
bash install_launchd.sh 21:00
```

- 毎日 21:00 に自動で記録します（時刻は `22:30` のように変えられます）。
- Mac がスリープ中だった場合は、次に起動・復帰したときに1回実行されます。電源が切れていた日は記録されません。
- 動いたかの確認：

```bash
tail -n 20 ~/esthe_data/run_log.txt
```

- 止めたいとき：

```bash
cd ~/esthe_tool && bash uninstall_launchd.sh
```

---

## 5. タスク4：集計とまとめ（数週間後）

```bash
cd ~/esthe_tool
.venv/bin/python trends.py          # 月ごとの波を最新にする（失敗したら --csv）
.venv/bin/python analyze.py
open ~/esthe_data/report/heatmap_weekday_hour.png
open -a TextEdit ~/esthe_data/report/report.md
```

できあがるもの（`~/esthe_data/report/`）:

- `report.md` … 日本語のまとめ（忙しい月／出勤が厚い・薄い曜日と時間帯／増員を考える時期の案）
- `heatmap_weekday_hour.png` … 曜日×時間の平均出勤人数（色が濃いほど多い）
- `weekday_hour_table.csv` … 上の数字の表

集計のルール:

- 同じ日について何回も記録しているので、**その日にいちばん近い記録**（確定に近い予定）だけを使います。まだ来ていない日は使いません。
- 各時間の「30分の時点で出勤している人数」を数え、曜日ごとに平均します。
- **キャストの名前は人数を数えるためだけに使い、結果には一切出しません。**
- データが4週間分に満たないときは、「まだ確かではない」という注意書きが自動で付きます。

> ⚠ ここでの「出勤人数」は**お店が混むと見込んで入れた人数**で、来店数そのものではありません。Google の検索量も関心の目安です。まとめはすべて**推定**として扱ってください。

### （任意）時間帯ごとの検索量も集める

Google トレンドは直近7日分なら1時間ごとの検索量を出せます。週に1回程度、次を実行してためておくと、`analyze.py` が「検索の割に出勤が少ない時間帯」も示します。

```bash
cd ~/esthe_tool && .venv/bin/python trends.py --hourly
```

---

## SSLエラーが出たとき

`SSLError` や `NotOpenSSLWarning` が出た場合は、専用環境を作り直してください。

```bash
cd ~/esthe_tool
rm -rf .venv
bash setup.sh
```

それでも直らない場合は、表示されたメッセージをそのまま共有してください。
