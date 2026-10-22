using Kiroku.Core;

namespace Kiroku.Server;

/// <summary>サービス停止中に、バックアップから復元する(新しいパソコンへの引っ越し・DB破損時)。</summary>
public static class RestoreCli
{
    public static int Run(DataPaths paths, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("使い方: Kiroku.Server.exe restore <バックアップファイル.krbk>"); return 2; }
        var file = args[0];
        if (!File.Exists(file)) { Console.WriteLine("ファイルが見つかりません: " + file); return 2; }
        Console.Write("復旧キー(40文字)を入力してください: ");
        var rk = Console.ReadLine() ?? "";
        paths.Ensure();
        var tmp = Path.Combine(paths.Tmp, "restore-" + Rand.Id() + ".db");
        try
        {
            var key = BackupCrypto.Decrypt(file, tmp, rk);
            if (!KirokuStore.VerifyFile(tmp, key)) { Console.WriteLine("バックアップの中身が壊れています。別の世代で試してください。"); return 1; }
            foreach (var ext in new[] { "", "-wal", "-shm" }) if (File.Exists(paths.Db + ext)) File.Delete(paths.Db + ext);
            File.Copy(tmp, paths.Db);
            var keys = new KeyStore(paths, KeyProtectors.ForServer(paths.Root));
            keys.StoreDbKey(key);
            keys.StoreRecoveryWrap(BackupCrypto.ReadWrap(file));
            Console.WriteLine("復元しました。サービスを開始してください。職員は再ログインが必要です。");
            return 0;
        }
        catch (KirokuException e) { Console.WriteLine(e.Message); return 1; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>管理者パスワードを忘れたとき。サービス停止中に、復旧キーを知っている人だけが再設定できる。</summary>
    public static int ResetAdmin(DataPaths paths, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("使い方: Kiroku.Server.exe reset-admin <ログインID>   (共有サービスを止めてから実行)"); return 2; }
        var keys = new KeyStore(paths, KeyProtectors.ForServer(paths.Root));
        var wrap = keys.LoadRecoveryWrap();
        if (wrap == null || !File.Exists(paths.Db)) { Console.WriteLine("このパソコンに記録データがありません。"); return 2; }
        try
        {
            Console.Write("復旧キー(40文字)を入力してください: ");
            RecoveryWrap.Unwrap(wrap, Console.ReadLine() ?? "");           // 復旧キーを知っていることの確認
            Console.Write("新しい一時パスワード(10文字以上)を入力してください: ");
            var pw = Console.ReadLine() ?? "";
            using var store = new KirokuStore(paths.Db, keys.LoadOrCreateDbKey());
            store.ResetPasswordByLoginId(args[0], pw);
            Console.WriteLine("再設定しました。次のログインで本人が新しいパスワードに変更します。");
            return 0;
        }
        catch (KirokuException e) { Console.WriteLine(e.Message); return 1; }
    }
}
