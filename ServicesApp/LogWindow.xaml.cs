using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Services.Core.Helpers;
using Services.Core.Services;
using WinRT.Interop;

namespace ServicesApp
{
    public sealed partial class LogWindow : Window
    {
        private readonly LogManager _logManager;
        private readonly string _serviceId;
        private DispatcherTimer? _timer;
        private AppWindow _appWindow;

        // 当前文件中最近的日志行（未过滤）与按 stdout/stderr 过滤后显示的行
        private readonly List<LogEntry> _allEntries = new();
        private readonly ObservableCollection<LogEntry> _logEntries = new();
        private long _lastPosition = 0;
        private string? _currentLogPath;
        private const int MaxLogLines = 3000;
        private ScrollViewer? _scrollViewer;

        public LogWindow(string serviceId, string displayName, LogManager logManager)
        {
            this.InitializeComponent();
            _serviceId = serviceId;
            _logManager = logManager;

            Title = $"日志 - {displayName}";
            TitleText.Text = Title;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));

            // Bind ListView
            LogListView.ItemsSource = _logEntries;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += OnTimerTick;

            this.Closed += OnWindowClosed;
            RootGrid.ActualThemeChanged += OnActualThemeChanged;

            LoadLog(true);
            _timer.Start();
        }

        private void OnTimerTick(object? sender, object e)
        {
            LoadLog(false);
        }

        private void OnWindowClosed(object sender, WindowEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Tick -= OnTimerTick;
                _timer.Stop();
                _timer = null;
            }
            RootGrid.ActualThemeChanged -= OnActualThemeChanged;
            this.Closed -= OnWindowClosed;
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            // 重新生成列表项以按新主题配色渲染 ANSI 颜色
            RebuildVisibleEntries();
        }

        private void OnStreamFilterChanged(object sender, RoutedEventArgs e)
        {
            RebuildVisibleEntries();
        }

        private bool ShouldShow(LogEntry entry)
        {
            return entry.Kind switch
            {
                LogLineKind.Stdout => StdoutCheck?.IsChecked != false,
                LogLineKind.Stderr => StderrCheck?.IsChecked != false,
                _ => true
            };
        }

        private void RebuildVisibleEntries()
        {
            _logEntries.Clear();
            foreach (var entry in _allEntries)
            {
                if (ShouldShow(entry)) _logEntries.Add(entry);
            }

            if (_logEntries.Count > 0)
            {
                LogListView.ScrollIntoView(_logEntries[_logEntries.Count - 1]);
            }
        }

        private void ClearEntries()
        {
            _allEntries.Clear();
            _logEntries.Clear();
        }

        private void AddEntry(LogEntry entry)
        {
            _allEntries.Add(entry);
            if (_allEntries.Count > MaxLogLines)
            {
                var removed = _allEntries[0];
                _allEntries.RemoveAt(0);
                if (_logEntries.Count > 0 && ReferenceEquals(_logEntries[0], removed))
                {
                    _logEntries.RemoveAt(0);
                }
            }

            if (ShouldShow(entry)) _logEntries.Add(entry);
        }

        private void LoadLog(bool forceReload)
        {
            try
            {
                var path = _logManager.GetLatestLogPath(_serviceId);

                // 文件轮转或强制刷新
                if (path != _currentLogPath || forceReload)
                {
                    ClearEntries();
                    _lastPosition = 0;
                    _currentLogPath = path;
                }

                if (string.IsNullOrEmpty(_currentLogPath) || !File.Exists(_currentLogPath))
                {
                    if (_allEntries.Count == 0) AddEntry(new LogEntry(LogLineKind.System, $"等待日志生成... (路径: {_logManager.GetLogDirectory()})"));
                    return;
                }

                using var fs = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // 文件被截断或轮转
                if (fs.Length < _lastPosition)
                {
                    ClearEntries();
                    _lastPosition = 0;
                }

                if (fs.Length > _lastPosition)
                {
                    bool shouldAutoScroll = IsScrolledToBottom();
                    
                    fs.Seek(_lastPosition, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);

                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        AddEntry(LogEntry.Parse(line));
                    }
                    _lastPosition = fs.Position;

                    if (shouldAutoScroll && _logEntries.Count > 0)
                    {
                        LogListView.ScrollIntoView(_logEntries.Last());
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Read log failed: {ex.Message}");
            }
        }

        private bool IsScrolledToBottom()
        {
            if (_scrollViewer == null)
            {
                _scrollViewer = FindVisualChild<ScrollViewer>(LogListView);
            }
            
            if (_scrollViewer == null || _logEntries.Count == 0)
            {
                return true;
            }
            
            return _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 10;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }
                
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadLog(true);
        }

        private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = _logManager.GetLogDirectory();
            if (System.IO.Directory.Exists(folder))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(folder);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        public void CenterOnScreen(AppWindow mainWindow)
        {
            if (mainWindow == null) return;

            var mainPos = mainWindow.Position;
            var mainSize = mainWindow.Size;
            var mySize = _appWindow.Size;

            var x = mainPos.X + (mainSize.Width - mySize.Width) / 2;
            var y = mainPos.Y + (mainSize.Height - mySize.Height) / 2;

            _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
    }
}