using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using tgdl.Download;
using tgdl.Mtproto;
using tgdl.Storage;

namespace tgdl.ViewModels;

/// <summary>账号列表里的一行。</summary>
public sealed partial class AccountItemViewModel : ObservableObject
{
    private readonly Action<AccountItemViewModel> _onRemove;
    private readonly Action<AccountItemViewModel> _onVerify;

    public AccountItemViewModel(Account account, bool isActive, Action<AccountItemViewModel> onRemove,
        Action<AccountItemViewModel> onVerify)
    {
        Account = account;
        IsActive = isActive;
        _onRemove = onRemove;
        _onVerify = onVerify;
    }

    public Account Account { get; }
    public bool IsActive { get; }

    public string Title => Account.Title;
    public string ShortKey => Account.ShortKey + "…";

    public string Subtitle
    {
        get
        {
            var id = Account.UserId > 0 ? $"user_id {Account.UserId}" : "未验证";
            var user = string.IsNullOrWhiteSpace(Account.Username) ? "" : $" @{Account.Username}";
            return $"{id}{user} · 主 DC{Account.HomeDc} · key {ShortKey}";
        }
    }

    /// <summary>这个账号手里有哪些 DC 的 key。</summary>
    public string DcsText
    {
        get
        {
            var parts = Account.DcIds.Select(dc => dc == Account.HomeDc ? $"DC{dc}(主)" : $"DC{dc}");
            return string.Join("  ", parts);
        }
    }

    public string LastOkText
    {
        get
        {
            if (Account.LastOkAt <= 0) return "";
            var t = DateTimeOffset.FromUnixTimeSeconds(Account.LastOkAt).ToLocalTime();
            return $"上次验证 {t:MM-dd HH:mm}";
        }
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    [RelayCommand]
    private void Verify() => _onVerify(this);
}

/// <summary>相册/多文件时的一条候选。</summary>
public sealed partial class MediaPickItemViewModel : ObservableObject
{
    public MediaPickItemViewModel(long chatId, MtpApi.MediaEntry entry)
    {
        ChatId = chatId;
        MessageId = entry.MessageId;
        Name = entry.FileName;
        Size = entry.Size;
        IsPhoto = entry.IsPhoto;
        Selected = true;
    }

    public long ChatId { get; }
    public int MessageId { get; }
    public string Name { get; }
    public long Size { get; }
    public bool IsPhoto { get; }

    [ObservableProperty] public partial bool Selected { get; set; }

    public string SizeText => DownloadEngine.FormatSize(Size);
    public string Subtitle => $"{ChatId}:{MessageId}  ·  {SizeText}";
    public string KindText => IsPhoto ? "图片" : "文件";
}
