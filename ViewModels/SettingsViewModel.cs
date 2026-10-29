using CommunityToolkit.Mvvm.ComponentModel;
using tgdl.Storage;

namespace tgdl.ViewModels;

/// <summary>设置页（改完点保存才落盘）。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] public partial decimal? AccountCount { get; set; } = 3;
    [ObservableProperty] public partial decimal? ConcurrencyPerAccount { get; set; } = 3;
    [ObservableProperty] public partial decimal? ChunkKb { get; set; } = 512;
    [ObservableProperty] public partial string DownloadDir { get; set; } = "";
    [ObservableProperty] public partial bool AdaptiveFlood { get; set; } = true;
    [ObservableProperty] public partial bool OpenFolderWhenDone { get; set; }
    [ObservableProperty] public partial string ApiId { get; set; } = "2040";
    [ObservableProperty] public partial string ApiHash { get; set; } = "";
    [ObservableProperty] public partial string Hint { get; set; } = "";

    public void LoadFrom(AppSettings s)
    {
        AccountCount = s.AccountCount;
        ConcurrencyPerAccount = s.ConcurrencyPerAccount;
        ChunkKb = s.ChunkSize / 1024;
        DownloadDir = s.DownloadDir;
        AdaptiveFlood = s.AdaptiveFlood;
        OpenFolderWhenDone = s.OpenFolderWhenDone;
        ApiId = s.ApiId.ToString();
        ApiHash = s.ApiHash;
        UpdateHint();
    }

    partial void OnAccountCountChanged(decimal? value) => UpdateHint();
    partial void OnConcurrencyPerAccountChanged(decimal? value) => UpdateHint();

    private void UpdateHint()
    {
        int acc = (int)(AccountCount ?? 1);
        int conc = (int)(ConcurrencyPerAccount ?? 1);
        int total = acc * conc;
        Hint = $"总并发 = {acc} 个账号 × 每个 {conc} = {total} 个在途请求" +
               (total > 30 ? "（偏高，容易撞 FLOOD_WAIT，建议 12~24）" : "（建议 12~24）");
    }

    public AppSettings ToSettings()
    {
        var s = SettingsStore.Current.Clone();
        s.AccountCount = (int)decimal.Clamp(AccountCount ?? 3, 1, 12);
        s.ConcurrencyPerAccount = (int)decimal.Clamp(ConcurrencyPerAccount ?? 3, 1, 10);
        s.ChunkSize = (int)decimal.Clamp(ChunkKb ?? 512, 16, 1024) * 1024;
        s.DownloadDir = DownloadDir.Trim();
        s.AdaptiveFlood = AdaptiveFlood;
        s.OpenFolderWhenDone = OpenFolderWhenDone;
        if (int.TryParse(ApiId.Trim(), out var apiId) && apiId > 0) s.ApiId = apiId;
        if (!string.IsNullOrWhiteSpace(ApiHash)) s.ApiHash = ApiHash.Trim();
        return s;
    }
}
