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
    /// 列头排序：点击切换升序 → 降序 → 默认顺序（添加顺序）
    /// </summary>
    public sealed partial class MainWindow
    {
        private enum SortColumn { Default, Name, Status, Path }
        private SortColumn _sortColumn = SortColumn.Default;
        private bool _sortDescending;

        private void OnSortHeaderClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag || !Enum.TryParse<SortColumn>(tag, out var column)) return;

            // 同一列循环：升序 → 降序 → 默认（添加顺序）
            if (column != _sortColumn)
            {
                _sortColumn = column;
                _sortDescending = false;
            }
            else if (!_sortDescending)
            {
                _sortDescending = true;
            }
            else
            {
                _sortColumn = SortColumn.Default;
                _sortDescending = false;
            }

            UpdateSortIcons();
            ApplySort();
            UpdateStatus(_sortColumn == SortColumn.Default
                ? "已恢复默认排序（添加顺序）。"
                : $"按{GetSortColumnText(_sortColumn)}{(_sortDescending ? "降序" : "升序")}排列。");
        }

        private static string GetSortColumnText(SortColumn column) => column switch
        {
            SortColumn.Name => "服务名称",
            SortColumn.Status => "状态",
            SortColumn.Path => "程序路径",
            _ => "添加顺序"
        };

        private void UpdateSortIcons()
        {
            void Set(FontIcon icon, SortColumn column)
            {
                bool active = _sortColumn == column;
                icon.Glyph = !active ? "\uE8CB" : (_sortDescending ? "\uE74B" : "\uE74A");
                icon.Opacity = active ? 0.8 : 0.3;
            }

            Set(NameSortIcon, SortColumn.Name);
            Set(StatusSortIcon, SortColumn.Status);
            Set(PathSortIcon, SortColumn.Path);
        }

        private int CompareServices(Service a, Service b)
        {
            int result = _sortColumn switch
            {
                SortColumn.Name => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                SortColumn.Status => GetStatusRank(a.State).CompareTo(GetStatusRank(b.State)),
                SortColumn.Path => StringComparer.OrdinalIgnoreCase.Compare(a.ExePath, b.ExePath),
                _ => 0
            };
            if (_sortDescending) result = -result;
            if (result != 0) return result;

            // 默认顺序：按添加时间
            result = a.CreatedAt.CompareTo(b.CreatedAt);
            return result != 0 ? result : string.CompareOrdinal(a.Id, b.Id);
        }

        private static int GetStatusRank(ServiceRunState state) => state switch
        {
            ServiceRunState.Running => 0,
            ServiceRunState.StartPending => 1,
            ServiceRunState.StopPending => 2,
            ServiceRunState.Stopped => 3,
            _ => 4
        };

        /// <summary>
        /// 原地调整 Services 顺序（使用 Move 保留已有列表项，避免整表重建）
        /// </summary>
        private void ApplySort()
        {
            var sorted = Services.ToList();
            sorted.Sort(CompareServices);

            for (int i = 0; i < sorted.Count; i++)
            {
                int current = Services.IndexOf(sorted[i]);
                if (current != i) Services.Move(current, i);
            }
        }
    }
}
