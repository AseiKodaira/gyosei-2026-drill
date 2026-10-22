using System.Globalization;
using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.App;

/// <summary>実行中の端末状態(設定・親機との接続・ログイン中の職員)。</summary>
internal sealed class AppState
{
    public LocalState Local { get; } = new(LocalState.DefaultDir, new DpapiProtector(machineScope: true));
    public DraftStore Drafts { get; } = new(DraftStore.DefaultDir, new DpapiProtector(machineScope: false));
    public ClientSettings Settings { get; set; } = new();
    public KirokuApi Api { get; set; } = null!;
    public StaffInfo Staff { get; set; } = null!;
    public bool IsParent => Settings.Mode == "parent";

    public void SaveSettings() => Local.Save(Settings);

    public static string ModeHintFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KirokuApp", "mode.txt");

    public static string DayText(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string DayLabel(DateTime d) => d.ToString("yyyy年M月d日（dddd）", new CultureInfo("ja-JP"));
}
