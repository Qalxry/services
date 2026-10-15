using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Services.Core.Application;
using Services.Core.Helpers;
using Services.Core.Services;
using Services.Core.Models;
using Services.Core.Presentation;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;

namespace ServicesApp
{
    /// <summary>
    /// 托盘图标与菜单
    /// </summary>
    public sealed partial class MainWindow
    {
        private void InitializeTrayIcon()
        {
            try
            {
                TrayIcon = new H.NotifyIcon.TaskbarIcon();
                TrayIcon.ToolTipText = "ServicesApp";

                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                }

                RootGrid.Children.Add(TrayIcon);
                TrayIcon.ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu;
                TrayIcon.NoLeftClickDelay = true;

                // H.NotifyIcon 默认禁用 WinRT 事件（Tapped/DoubleTapped 不会触发），需通过命令响应托盘点击
                var trayClickCommand = new XamlUICommand();
                trayClickCommand.ExecuteRequested += (s, e) => DispatcherQueue.TryEnqueue(ShowWindow);
                TrayIcon.LeftClickCommand = trayClickCommand;
                TrayIcon.DoubleClickCommand = trayClickCommand;

                var flyout = new MenuFlyout();

                var showCommand = new XamlUICommand();
                showCommand.Label = "显示窗口";
                showCommand.ExecuteRequested += (s, e) => ShowWindow();
                var showItem = new MenuFlyoutItem { Text = "显示窗口", Command = showCommand };

                var exitCommand = new XamlUICommand();
                exitCommand.Label = "退出";
                exitCommand.ExecuteRequested += (s, e) => RealExit();
                var exitItem = new MenuFlyoutItem { Text = "退出", Command = exitCommand };

                flyout.Items.Add(showItem);
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(exitItem);

                TrayIcon.ContextFlyout = flyout;
                TrayIcon.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray Icon Init Failed: {ex}");
            }
        }
    }
}
