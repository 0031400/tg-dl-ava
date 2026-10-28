using CommunityToolkit.Mvvm.ComponentModel;
using tgdl.Storage;

namespace tgdl.ViewModels;

/// <summary>设置页（改完点保存才落盘）。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] public partial decimal? AuthKeyCount { get; set; } = 3;
    [ObservableProperty] public partial decimal? ConcurrencyPerKey { get; set; } = 4;
    [ObservableProperty] public partial decimal? ChunkKb { get; set; } = 512;
    [ObservableProperty] public partial string DownloadDir { get; set; } = "";
    [ObservableProperty] public partial bool AdaptiveFlood { get; set; } = true;
    [ObservableProperty] public partial bool OpenFolderWhenDone { get; set; }
    [ObservableProperty] public partial string ApiId { get; set; } = "2040";
    [ObservableProperty] public partial string ApiHash { get; set; } = "";
    [ObservableProperty] public partial string Hint { get; set; } = "";

    public void LoadFrom(AppSettings s)
    {
        AuthKeyCount = s.AuthKeyCount;
        ConcurrencyPerKey = s.ConcurrencyPerKey;
        ChunkKb = s.ChunkSize / 1024;
        DownloadDir = s.DownloadDir;
        AdaptiveFlood = s.AdaptiveFlood;
        OpenFolderWhenDone = s.OpenFolderWhenDone;
        ApiId = s.ApiId.ToString();
        ApiHash = s.ApiHash;
        UpdateHint();
    }

    partial void OnAuthKeyCountChanged(decimal? value) => UpdateHint();
    partial void OnConcurrencyPerKeyChanged(decimal? value) => UpdateHint();

    private void UpdateHint()
    {
        int keys = (int)(AuthKeyCount ?? 1);
        int conc = (int)(ConcurrencyPerKey ?? 1);
        int total = keys * conc;
        Hint = $"总并发 = {keys} × {conc} = {total} 个在途请求" +
               (total > 32 ? "（超过 32 很容易撞 FLOOD_WAIT，建议 8~24）" : "（建议 ≤ 32）");
    }

    public AppSettings ToSettings()
    {
        var s = SettingsStore.Current.Clone();
        s.AuthKeyCount = (int)decimal.Clamp(AuthKeyCount ?? 3, 1, 12);
        s.ConcurrencyPerKey = (int)decimal.Clamp(ConcurrencyPerKey ?? 4, 1, 32);
        s.ChunkSize = (int)decimal.Clamp(ChunkKb ?? 512, 16, 1024) * 1024;
        s.DownloadDir = DownloadDir.Trim();
        s.AdaptiveFlood = AdaptiveFlood;
        s.OpenFolderWhenDone = OpenFolderWhenDone;
        if (int.TryParse(ApiId.Trim(), out var apiId) && apiId > 0) s.ApiId = apiId;
        if (!string.IsNullOrWhiteSpace(ApiHash)) s.ApiHash = ApiHash.Trim();
        return s;
    }
}
