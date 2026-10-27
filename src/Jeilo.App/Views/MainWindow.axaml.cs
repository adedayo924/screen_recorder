using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Jeilo.App.ViewModels;

namespace Jeilo.App.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.RequestMinimize += () =>
        {
            WindowState = WindowState.Minimized;
        };

        viewModel.RequestRestore += () =>
        {
            WindowState = WindowState.Normal;
            Activate();
        };

        viewModel.RequestSetClipboardText += text =>
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch
            {
                // Clipboard fallback
            }
        };

        viewModel.RequestConfirmDelete += async (item, callback) =>
        {
            var confirmed = await ConfirmDeleteAsync(item.FileName);
            callback(confirmed);
        };
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where recordings are saved",
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return;
        }

        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SetOutputDirectory(path);
        }
    }

    private void OpenRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecordingItem item } && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenRecordingCommand.Execute(item);
        }
    }

    private void RecordingThumbnail_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: RecordingItem item } source ||
            DataContext is not MainWindowViewModel viewModel ||
            !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            return;
        }

        viewModel.OpenRecordingCommand.Execute(item);
        e.Handled = true;
    }

    private void ShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecordingItem item } && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ShowInFolderCommand.Execute(item);
        }
    }

    private void CopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecordingItem item } && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CopyRecordingPathCommand.Execute(item);
        }
    }

    private void DeleteRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecordingItem item } && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteRecordingCommand.Execute(item);
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string fileName)
    {
        var dialog = new Window
        {
            Title = "Delete recording",
            Icon = Icon,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelButton.Classes.Add("secondary");

        var deleteButton = new Button
        {
            Content = "Delete",
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        deleteButton.Classes.Add("danger");

        cancelButton.Click += (_, _) => dialog.Close(false);
        deleteButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "Delete this recording?",
                    FontSize = 17,
                    FontWeight = Avalonia.Media.FontWeight.Bold
                },
                new TextBlock
                {
                    Text = $"Are you sure you want to permanently delete \"{fileName}\"? This cannot be undone.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, deleteButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            if (viewModel.IsRecording)
            {
                var stop = await ConfirmStopAsync();
                if (!stop)
                {
                    _closing = false;
                    return;
                }
            }

            _allowClose = true;
            await viewModel.ShutdownAsync();
        }
        finally
        {
            if (_allowClose)
            {
                Close();
            }
            else
            {
                _closing = false;
            }
        }
    }

    private async Task<bool> ConfirmStopAsync()
    {
        var dialog = new Window
        {
            Title = "Recording in progress",
            Icon = Icon,
            Width = 410,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var keepButton = new Button
        {
            Content = "Keep recording",
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        keepButton.Classes.Add("secondary");

        var stopButton = new Button
        {
            Content = "Stop and close",
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        stopButton.Classes.Add("primary");

        keepButton.Click += (_, _) => dialog.Close(false);
        stopButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "A recording is still running",
                    FontSize = 17,
                    FontWeight = Avalonia.Media.FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Stop the recording and save the video before closing Jeilo Recorder?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { keepButton, stopButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }
}
