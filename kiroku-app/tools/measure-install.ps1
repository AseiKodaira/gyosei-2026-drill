# インストール~初回接続の所要時間を測るための記録用スクリプト(実機で、管理者のPowerShellから)。
# 使い方: .\measure-install.ps1  → 各工程の開始時に Enter を押す。最後に合計(分)を表示。
$steps = @("1 インストーラー起動~役割選択~インストール完了", "2 (親機)管理者登録~復旧キー保管", "3 (子機)親機検出~接続コード入力", "4 親機での承認", "5 職員ログイン~日々の記録画面表示")
$t = @()
foreach ($s in $steps) { Read-Host "[$s] を始めるとき Enter" | Out-Null; $t += Get-Date }
Read-Host "すべて終わったら Enter" | Out-Null; $t += Get-Date
for ($i = 0; $i -lt $steps.Count; $i++) { "{0}: {1:N1} 分" -f $steps[$i], ($t[$i+1] - $t[$i]).TotalMinutes }
"合計: {0:N1} 分" -f ($t[-1] - $t[0]).TotalMinutes
