using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServicesApp
{
    /// <summary>
    /// 按顺序逐行追加控件的等宽多列表单（取代手写 Grid 行号：插入或调整一行不必修改其后所有行号）
    /// </summary>
    internal sealed class FormRows
    {
        private readonly int _columns;

        public Grid Grid { get; }

        public FormRows(int columns, double spacing)
        {
            _columns = columns;
            Grid = new Grid { RowSpacing = spacing, ColumnSpacing = spacing };
            for (int i = 0; i < columns; i++)
                Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        /// <summary>
        /// 追加一行。只传一个控件时它横跨所有列；传多个控件时依次放入各列（null 表示该列留空）。
        /// </summary>
        public void Add(params FrameworkElement?[] cells)
        {
            int row = Grid.RowDefinitions.Count;
            Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int column = 0; column < cells.Length && column < _columns; column++)
            {
                var cell = cells[column];
                if (cell == null) continue;

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                if (cells.Length == 1) Grid.SetColumnSpan(cell, _columns);
                Grid.Children.Add(cell);
            }
        }
    }
}
