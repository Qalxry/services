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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;

namespace ServicesApp
{
    /// <summary>
    /// 列表视图：搜索 / 状态筛选与列头排序（点击切换升序 → 降序 → 默认顺序即添加顺序）。
    /// _allServices 保存全部服务，Services 只包含筛选后的项并按当前顺序排列。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly List<Service> _allServices = new();
        private ServiceFilter _filter = ServiceFilter.None;

        private enum SortColumn { Default, Name, Status, Uptime, Path }
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
            ApplyView();
            UpdateStatus(_sortColumn == SortColumn.Default
                ? "已恢复默认排序（添加顺序）。"
                : $"按{GetSortColumnText(_sortColumn)}{(_sortDescending ? "降序" : "升序")}排列。");
        }

        private static string GetSortColumnText(SortColumn column) => column switch
        {
            SortColumn.Name => "服务名称",
            SortColumn.Status => "状态",
            SortColumn.Uptime => "运行时长",
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
            Set(UptimeSortIcon, SortColumn.Uptime);
            Set(PathSortIcon, SortColumn.Path);
        }

        private int CompareServices(Service a, Service b)
        {
            int result = _sortColumn switch
            {
                SortColumn.Name => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                SortColumn.Status => GetStatusRank(a.State).CompareTo(GetStatusRank(b.State)),
                SortColumn.Uptime => (a.Uptime ?? TimeSpan.MinValue).CompareTo(b.Uptime ?? TimeSpan.MinValue),
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
        /// 按筛选条件与排序重新生成 Services：原地插入 / 移动 / 删除，保留已有列表项，避免整表重建
        /// </summary>
        private void ApplyView()
        {
            var desired = _allServices.Where(_filter.Matches).ToList();
            desired.Sort(CompareServices);

            for (int i = 0; i < desired.Count; i++)
            {
                if (i < Services.Count && ReferenceEquals(Services[i], desired[i])) continue;
                int current = Services.IndexOf(desired[i]);
                if (current >= 0) Services.Move(current, i);
                else Services.Insert(i, desired[i]);
            }
            while (Services.Count > desired.Count) Services.RemoveAt(Services.Count - 1);
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => UpdateFilter();

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => UpdateFilter();

        private void UpdateFilter()
        {
            // 控件在 InitializeComponent 期间就会触发事件
            if (SearchBox == null || StateFilterBox == null) return;

            _filter = new ServiceFilter(SearchBox.Text, StateFilterBox.SelectedIndex switch
            {
                1 => StateFilter.Running,
                2 => StateFilter.Stopped,
                3 => StateFilter.Other,
                _ => StateFilter.All
            });
            ApplyView();
            if (_filter.IsActive) UpdateStatus($"筛选：显示 {Services.Count} / {_allServices.Count} 个服务。");
        }
    }
}
