using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace tgdl.ViewModels;

public sealed partial class AuthKeyItemViewModel : ObservableObject
{
    private readonly Action<AuthKeyItemViewModel> _onRemove;

    public AuthKeyItemViewModel(int dcId, string hex, bool isPrimary, Action<AuthKeyItemViewModel> onRemove)
    {
        DcId = dcId;
        Hex = hex;
        IsPrimary = isPrimary;
        _onRemove = onRemove;
    }

    public int DcId { get; }
    public string Hex { get; }
    public bool IsPrimary { get; }

    public string ShortHex => Hex.Length >= 16 ? Hex.Substring(0, 16) : Hex;
    public string Tag => IsPrimary ? "主" : "";

    [RelayCommand]
    private void Remove() => _onRemove(this);
}

public sealed partial class AuthKeyGroupViewModel : ObservableObject
{
    public AuthKeyGroupViewModel(int dcId, bool isHome)
    {
        DcId = dcId;
        IsHome = isHome;
    }

    public int DcId { get; }
    public bool IsHome { get; }
    public ObservableCollection<AuthKeyItemViewModel> Keys { get; } = new();

    public string Header => $"DC{DcId}" + (IsHome ? "（主 DC）" : "") + $" · {Keys.Count} 条";
}
