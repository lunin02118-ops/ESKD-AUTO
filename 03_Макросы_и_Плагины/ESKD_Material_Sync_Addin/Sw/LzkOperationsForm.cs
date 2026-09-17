using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Окно «Операции» кнопки «Ведомость ЛЗК» (ТЗ-02 Т-36): строка на модель изделия, колонка на операцию.
    /// У моделей без свойства «Операции» галочки предложены по признакам модели — строка помечена «(авто)».
    /// </summary>
    public sealed class LzkOperationsForm : Form
    {
        private const int FirstOperationColumn = 3;
        private readonly DataGridView _grid = new DataGridView();
        private readonly List<LzkItem> _items;

        public Dictionary<string, string> Result { get; private set; }

        public LzkOperationsForm(IList<LzkItem> items, IDictionary<string, ModelTraits> traits)
        {
            _items = items.OrderBy(i => i.IsAssembly ? 0 : 1).ThenBy(i => i.Designation, StringComparer.CurrentCultureIgnoreCase).ToList();
            Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Text = "ЕСКД: Ведомость ЛЗК — операции";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 600);
            MinimumSize = new Size(700, 350);
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = false;

            Label hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                Text = "Отметьте операции изготовления. Строки «(авто)» заполнены по модели — проверьте их. " +
                       "Выбор записывается в свойство «Операции» моделей изделия и сохраняется."
            };

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.BackgroundColor = SystemColors.Window;

            _grid.Columns.Add(TextColumn("Обозначение", 160));
            _grid.Columns.Add(TextColumn("Наименование", 220));
            _grid.Columns.Add(TextColumn("", 50));
            foreach (string op in LzkOperations.All)
            {
                DataGridViewCheckBoxColumn c = new DataGridViewCheckBoxColumn { HeaderText = op, Width = 82, SortMode = DataGridViewColumnSortMode.NotSortable };
                _grid.Columns.Add(c);
            }

            foreach (LzkItem item in _items)
            {
                ModelTraits t;
                if (!traits.TryGetValue(item.Path, out t)) t = new ModelTraits { IsAssembly = item.IsAssembly };
                bool auto = string.IsNullOrWhiteSpace(item.Operations);
                List<string> chosen = auto ? LzkOperations.Suggest(t) : LzkOperations.Parse(item.Operations);
                object[] values = new object[FirstOperationColumn + LzkOperations.All.Length];
                values[0] = item.Designation;
                values[1] = item.Name.Length > 0 ? item.Name : System.IO.Path.GetFileNameWithoutExtension(item.Path);
                values[2] = auto ? "(авто)" : "";
                for (int i = 0; i < LzkOperations.All.Length; i++)
                    values[FirstOperationColumn + i] = chosen.Contains(LzkOperations.All[i]);
                int row = _grid.Rows.Add(values);
                _grid.Rows[row].Tag = item;
                _grid.Rows[row].Cells[0].ToolTipText = item.Path;
                if (auto) _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 225);
                // Операции, которых нет в списке (введены вручную), сохраняются как есть.
                List<string> extra = chosen.Where(o => Array.IndexOf(LzkOperations.All, o) < 0).ToList();
                if (extra.Count > 0) _grid.Rows[row].Cells[2].ToolTipText = "Также: " + string.Join("; ", extra.ToArray());
            }

            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            Button cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 100 };
            Button ok = new Button { Text = "Сформировать", Width = 130 };
            ok.Click += OnOk;
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(_grid);
            Controls.Add(hint);
            Controls.Add(bottom);
        }

        private static DataGridViewTextBoxColumn TextColumn(string header, int width)
        {
            return new DataGridViewTextBoxColumn { HeaderText = header, Width = width, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable };
        }

        private void OnOk(object sender, EventArgs e)
        {
            _grid.EndEdit();
            List<string> empty = new List<string>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                LzkItem item = (LzkItem)row.Tag;
                List<string> ops = new List<string>();
                for (int i = 0; i < LzkOperations.All.Length; i++)
                {
                    object v = row.Cells[FirstOperationColumn + i].Value;
                    if (v is bool && (bool)v) ops.Add(LzkOperations.All[i]);
                }
                ops.AddRange(LzkOperations.Parse(item.Operations).Where(o => Array.IndexOf(LzkOperations.All, o) < 0));
                if (ops.Count == 0) empty.Add(item.Designation.Length > 0 ? item.Designation : item.Name);
                Result[item.Path] = LzkOperations.Join(ops);
            }
            if (empty.Count > 0)
            {
                string list = string.Join("\n", empty.Take(15).ToArray()) + (empty.Count > 15 ? "\n…" : "");
                if (MessageBox.Show(this, "Без операций (в ведомости будет «?»):\n\n" + list + "\n\nПродолжить?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
