using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Services.Core.Notifications;

namespace ServicesApp
{
    /// <summary>
    /// Webhook 事件的复选框组（设置对话框与服务编辑表单共用）
    /// </summary>
    internal sealed class WebhookEventChecks
    {
        private static readonly (string Event, string Label)[] Options =
        {
            (WebhookEvents.Failed, "以失败状态停止"),
            (WebhookEvents.Unhealthy, "健康检查失败"),
            (WebhookEvents.Restarting, "异常退出后重启")
        };

        private readonly List<(string Event, CheckBox Box)> _boxes = new();

        public FrameworkElement View { get; }

        public WebhookEventChecks(IEnumerable<string> selected)
        {
            var set = selected.ToHashSet();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var (name, label) in Options)
            {
                var box = new CheckBox { Content = label, IsChecked = set.Contains(name), MinWidth = 0 };
                _boxes.Add((name, box));
                panel.Children.Add(box);
            }
            View = panel;
        }

        /// <summary>选中的事件；一个都没选时为默认事件</summary>
        public List<string> Selected
        {
            get
            {
                var events = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Event).ToList();
                return events.Count == 0 ? WebhookEvents.Default.ToList() : events;
            }
        }
    }
}
