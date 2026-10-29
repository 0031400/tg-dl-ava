using System;
using CommunityToolkit.Mvvm.ComponentModel;
using tgdl.Mtproto;
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

    // ---- 代理 ----
    [ObservableProperty] public partial int ProxyModeIndex { get; set; }          // 0 直连 / 1 手动 / 2 系统
    [ObservableProperty] public partial int ProxyKindIndex { get; set; }          // 0 SOCKS5 / 1 HTTP
    [ObservableProperty] public partial string ProxyHost { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial decimal? ProxyPort { get; set; } = 1080;
    [ObservableProperty] public partial string ProxyUser { get; set; } = "";
    [ObservableProperty] public partial string ProxyPassword { get; set; } = "";
    [ObservableProperty] public partial string ProxyVlessLink { get; set; } = "";
    [ObservableProperty] public partial bool ProxyVlessEnabled { get; set; }
    /// <summary>手动模式下「代理类型」下拉可用。</summary>
    [ObservableProperty] public partial bool ProxyManualEnabled { get; set; } = true;
    /// <summary>手动 + SOCKS5/HTTP：显示地址/端口/用户名密码。</summary>
    [ObservableProperty] public partial bool ProxyEndpointVisible { get; set; } = true;
    /// <summary>手动 + VLESS：显示分享链接输入框。</summary>
    [ObservableProperty] public partial bool ProxyVlessVisible { get; set; }
    [ObservableProperty] public partial string ProxyHint { get; set; } = "";

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
        ProxyModeIndex = (int)s.ProxyMode;
        ProxyKindIndex = (int)s.ProxyKind;
        ProxyHost = s.ProxyHost;
        ProxyPort = s.ProxyPort;
        ProxyUser = s.ProxyUsername;
        ProxyPassword = s.ProxyPassword;
        ProxyVlessLink = s.ProxyVlessLink;
        UpdateHint();
        UpdateProxyHint();
    }

    partial void OnAccountCountChanged(decimal? value) => UpdateHint();
    partial void OnProxyModeIndexChanged(int value) => UpdateProxyHint();
    partial void OnProxyKindIndexChanged(int value) => UpdateProxyHint();
    partial void OnProxyVlessLinkChanged(string value) => UpdateProxyHint();
    partial void OnProxyHostChanged(string value) => UpdateProxyHint();
    partial void OnProxyPortChanged(decimal? value) => UpdateProxyHint();

    private void UpdateProxyHint()
    {
        var mode = (ProxyMode)ProxyModeIndex;
        ProxyManualEnabled = mode == ProxyMode.Manual;
        ProxyVlessVisible = mode == ProxyMode.Manual && ProxyKindIndex == 2;
        ProxyEndpointVisible = mode == ProxyMode.Manual && ProxyKindIndex != 2;
        ProxyVlessEnabled = ProxyVlessVisible;
        switch (mode)
        {
            case ProxyMode.None:
                ProxyHint = "直连。如果你那边直连连不上 Telegram，这里必须选代理。";
                break;
            case ProxyMode.Manual:
                if (ProxyKindIndex == 2)
                {
                    try
                    {
                        var v = VlessConfig.Parse(ProxyVlessLink);
                        ProxyHint = $"解析成功：{v.Display}（uuid {v.Uuid.Substring(0, 8)}…，{v.Transport}，path {v.WsPath}）";
                    }
                    catch (Exception ex)
                    {
                        ProxyHint = "VLESS 链接还没填或格式不对：" + ex.Message;
                    }
                }
                else
                {
                    ProxyHint = "所有连接（含 DH 握手）都走这个代理。HTTP 代理必须支持 CONNECT。";
                }
                break;
            default:
                var resolved = ProxyResolver.ResolveSystem();
                ProxyHint = resolved == null
                    ? "系统代理未启用（或没设置 ProxyServer），会退化成直连。"
                    : $"检测到系统代理：{resolved.Display}";
                break;
        }
    }
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
        s.ProxyMode = (ProxyMode)Math.Clamp(ProxyModeIndex, 0, 2);
        s.ProxyKind = (ProxyKind)Math.Clamp(ProxyKindIndex, 0, 2);   // 0 SOCKS5 / 1 HTTP / 2 VLESS
        s.ProxyHost = ProxyHost.Trim();
        s.ProxyPort = (int)decimal.Clamp(ProxyPort ?? 1080, 1, 65535);
        s.ProxyUsername = ProxyUser.Trim();
        s.ProxyPassword = ProxyPassword;
        s.ProxyVlessLink = ProxyVlessLink.Trim();
        return s;
    }
}
