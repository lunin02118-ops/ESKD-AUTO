using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Окно «Операции» кнопки «Ведомость ЛЗК» (ТЗ-02 Т-36): строка на модель изделия, колонка на операцию.
    /// У моделей без свойства «Операции» галочки предложены по признакам модели — строка помечена «(авто)».
    /// Слева в строке — миниатюра модели, справа — крупный эскиз выделенной строки (З-6): по одним шифрам
    /// расцеховку проверять трудно. Миниатюры грузятся в фоне и окно не задерживают.
    /// </summary>
    public sealed class LzkOperationsForm : Form
    {
        private const int FirstOperationColumn = 4;
        private const int ThumbSize = 64;
        private const int PreviewSize = 256;
        private readonly DataGridView _grid = new DataGridView();
        private readonly List<LzkItem> _items;
        private readonly Func<string, Image> _thumbnail;
        private readonly Dictionary<string, Image> _previews = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly PictureBox _preview = new PictureBox();
        private readonly Label _previewCaption = new Label();
        private readonly LzkInputs _inputs;
        private readonly NumericUpDown _quantity = new NumericUpDown();
        private readonly DateTimePicker _deadline = new DateTimePicker();
        private readonly TextBox _color = new TextBox();

        public Dictionary<string, string> Result { get; private set; }

        public LzkOperationsForm(IList<LzkItem> items, IDictionary<string, ModelTraits> traits)
            : this(items, traits, null)
        {
        }

        /// <param name="thumbnail">Эскиз модели по пути (до 256 px) или null; вызывается в фоновом потоке.</param>
        public LzkOperationsForm(IList<LzkItem> items, IDictionary<string, ModelTraits> traits, Func<string, Image> thumbnail)
            : this(items, traits, thumbnail, null)
        {
        }

        /// <param name="inputs">Тираж, срок, цвет книги ЛЗК (ТЗ-04 К-4): подставляются прошлые, по «Сформировать» пишутся обратно.</param>
        public LzkOperationsForm(IList<LzkItem> items, IDictionary<string, ModelTraits> traits, Func<string, Image> thumbnail,
            LzkInputs inputs)
        {
            _thumbnail = thumbnail;
            _inputs = inputs;
            _items = items.OrderBy(i => i.IsAssembly ? 0 : 1).ThenBy(i => i.Designation, StringComparer.CurrentCultureIgnoreCase).ToList();
            Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Text = "ЕСКД: Ведомость ЛЗК — операции";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1300, 700);
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

            FlowLayoutPanel order = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(8, 4, 8, 0),
                WrapContents = false,
                Visible = inputs != null
            };
            if (inputs != null)
            {
                _quantity.Minimum = 1;
                _quantity.Maximum = 100000;
                _quantity.Width = 80;
                _quantity.Value = Math.Max(1, Math.Min(100000, inputs.Quantity));
                _deadline.Format = DateTimePickerFormat.Short;
                _deadline.ShowCheckBox = true;
                _deadline.Width = 130;
                _deadline.Value = inputs.Deadline ?? DateTime.Today.AddDays(30);
                _deadline.Checked = inputs.Deadline.HasValue;
                _color.Width = 140;
                _color.Text = inputs.Color;
                order.Controls.Add(Caption("Изделий в заказе:"));
                order.Controls.Add(_quantity);
                order.Controls.Add(Caption("   Срок отгрузки:"));
                order.Controls.Add(_deadline);
                order.Controls.Add(Caption("   Цвет покраски (RAL):"));
                order.Controls.Add(_color);
                order.Controls.Add(Caption("   — их можно поменять и в книге, лист «Паспорт»."));
            }

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
            _grid.RowTemplate.Height = ThumbSize + 4;

            DataGridViewImageColumn sketch = new DataGridViewImageColumn
            {
                HeaderText = "Эскиз",
                Width = ThumbSize + 8,
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            // Без этого пустая клетка рисует значок «нет картинки» — красный крест.
            sketch.DefaultCellStyle.NullValue = null;
            _grid.Columns.Add(sketch);
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
                values[0] = null;
                values[1] = item.Designation;
                values[2] = item.Name.Length > 0 ? item.Name : System.IO.Path.GetFileNameWithoutExtension(item.Path);
                values[3] = auto ? "(авто)" : "";
                for (int i = 0; i < LzkOperations.All.Length; i++)
                    values[FirstOperationColumn + i] = chosen.Contains(LzkOperations.All[i]);
                int row = _grid.Rows.Add(values);
                _grid.Rows[row].Tag = item;
                _grid.Rows[row].Cells[1].ToolTipText = item.Path;
                if (auto) _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 225);
                // Операции, которых нет в списке (введены вручную), сохраняются как есть.
                List<string> extra = chosen.Where(o => Array.IndexOf(LzkOperations.All, o) < 0).ToList();
                if (extra.Count > 0) _grid.Rows[row].Cells[3].ToolTipText = "Также: " + string.Join("; ", extra.ToArray());
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

            Panel side = new Panel { Dock = DockStyle.Right, Width = PreviewSize + 24, Padding = new Padding(8) };
            _preview.Dock = DockStyle.Top;
            _preview.Height = PreviewSize;
            _preview.SizeMode = PictureBoxSizeMode.Zoom;
            _preview.BackColor = Color.White;
            _preview.BorderStyle = BorderStyle.FixedSingle;
            _previewCaption.Dock = DockStyle.Top;
            _previewCaption.Height = 52;
            _previewCaption.Padding = new Padding(0, 6, 0, 0);
            side.Controls.Add(_previewCaption);
            side.Controls.Add(_preview);

            Controls.Add(_grid);
            Controls.Add(side);
            Controls.Add(order);
            Controls.Add(hint);
            Controls.Add(bottom);

            _grid.SelectionChanged += delegate { ShowPreview(_grid.CurrentRow); };
            _grid.CellMouseEnter += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0) ShowPreview(_grid.Rows[e.RowIndex]);
            };
            _grid.MouseLeave += delegate { ShowPreview(_grid.CurrentRow); };
            // Исполнения одного файла — отдельные строки, а «Операции» у файла общие: галочка переносится во все его строки.
            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += OnOperationChanged;
            Shown += delegate { LoadThumbnails(); };
            FormClosed += delegate { DisposePreviews(); };
        }

        private bool _mirroring;

        private void OnOperationChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_mirroring || e.RowIndex < 0 || e.ColumnIndex < FirstOperationColumn) return;
            LzkItem item = _grid.Rows[e.RowIndex].Tag as LzkItem;
            if (item == null) return;
            object value = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            _mirroring = true;
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    LzkItem other = row.Tag as LzkItem;
                    if (row.Index != e.RowIndex && other != null && string.Equals(other.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                        row.Cells[e.ColumnIndex].Value = value;
                }
            }
            finally
            {
                _mirroring = false;
            }
        }

        /// <summary>Крупный эскиз и подпись строки — справа от таблицы.</summary>
        private void ShowPreview(DataGridViewRow row)
        {
            LzkItem item = row != null ? row.Tag as LzkItem : null;
            if (item == null)
            {
                _preview.Image = null;
                _previewCaption.Text = "";
                return;
            }
            Image image;
            bool loaded = _previews.TryGetValue(item.Path, out image);
            _preview.Image = image;
            _previewCaption.Text = (item.Designation + " " + item.Name).Trim() +
                (loaded && image == null ? Environment.NewLine + "Эскиза в файле нет" : "");
        }

        /// <summary>
        /// Миниатюры читаются в отдельном STA-потоке (обработчик миниатюр Windows — COM), по одной на модель,
        /// и подставляются в строки по мере готовности; закрытое окно дальнейшие картинки не принимает.
        /// </summary>
        private void LoadThumbnails()
        {
            if (_thumbnail == null) return;
            List<string> paths = _items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Thread worker = new Thread(delegate()
            {
                foreach (string path in paths)
                {
                    if (IsDisposed) return;
                    Image image = null;
                    try
                    {
                        image = _thumbnail(path);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Эскиз " + path, ex);
                    }
                    string p = path;
                    Image img = image;
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate { PutThumbnail(p, img); }));
                    }
                    catch (InvalidOperationException)
                    {
                        // Окно уже закрыто — картинка никому не нужна.
                        if (img != null) img.Dispose();
                        return;
                    }
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void PutThumbnail(string path, Image image)
        {
            if (IsDisposed)
            {
                if (image != null) image.Dispose();
                return;
            }
            _previews[path] = image;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                LzkItem item = row.Tag as LzkItem;
                if (item != null && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) row.Cells[0].Value = image;
            }
            ShowPreview(_grid.CurrentRow);
        }

        private void DisposePreviews()
        {
            _preview.Image = null;
            foreach (DataGridViewRow row in _grid.Rows) row.Cells[0].Value = null;
            foreach (Image image in _previews.Values)
                if (image != null) image.Dispose();
            _previews.Clear();
        }

        private static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
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
            if (_inputs != null)
            {
                _inputs.Quantity = (int)_quantity.Value;
                _inputs.Deadline = _deadline.Checked ? (DateTime?)_deadline.Value.Date : null;
                _inputs.Color = _color.Text.Trim();
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
