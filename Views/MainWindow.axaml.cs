using System;
using Avalonia.Controls;
using tgdl.ViewModels;

namespace tgdl.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.LogAppended += ScrollLogToEnd;
    }

    private void ScrollLogToEnd()
    {
        var box = this.FindControl<TextBox>("LogBox");
        if (box == null) return;
        box.CaretIndex = box.Text?.Length ?? 0;
    }
}
