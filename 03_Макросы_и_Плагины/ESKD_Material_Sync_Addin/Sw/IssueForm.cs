using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Окно кнопки К-5 «Выдать в производство» (ТЗ-02 Т-38) и окно «Цвета покраски» (Т-14в) одним целым:
    /// состав заказа с тиражом слева, цвета окрашиваемых единиц справа. Оба списка подставляются из
    /// прошлой сводной заявки — при повторной выдаче обычно меняется только тираж.
    /// </summary>
    public sealed class IssueForm : Form
    {
        private readonly TextBox _order = new TextBox();
        private readonly TextBox _number = new TextBox();
        private readonly ListView _products = new ListView();
        private readonly ComboBox _color = new ComboBox();
        private readonly CheckBox _draft = new CheckBox();

        /// <summary>Папка заказа.</summary>
        public string Order { get { return _order.Text.Trim(); } }
        /// <summary>№ заявки.</summary>
        public string Number { get { return _number.Text.Trim(); } }
        /// <summary>Цвет, который получат единицы без своего цвета.</summary>
        public string Color { get { return _color.Text.Trim(); } }
        /// <summary>Черновик сводной: собрать книгу и остановиться (Т-38).</summary>
        public bool Draft { get { return _draft.Checked; } }

        /// <summary>Состав: «И01_…=2;И02_…=1» — в том виде, в каком его принимает IssueService.</summary>
        public string Quantities
        {
            get
            {
                List<string> parts = new List<string>();
                foreach (ListViewItem item in _products.Items)
                {
                    if (!item.Checked) continue;
                    int quantity;
                    if (!int.TryParse(item.SubItems[1].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity))
                        quantity = 1;
                    parts.Add(item.Text + "=" + Math.Max(1, quantity));
                }
                return string.Join(";", parts.ToArray());
            }
        }

        /// <summary>Цвета покраски: RAL и фактура из прошлой сводной (Т-14в).</summary>
        public static readonly string[] KnownColors =
        {
            "RAL 7035 шагрень", "RAL 9005 шагрень", "RAL 9016 гладкая", "RAL 5005 гладкая",
            "RAL 3020 гладкая", "RAL 1018 шагрень", "цинк без покраски"
        };

        public IssueForm(string orderFolder)
        {
            Text = "ЕСКД: выдать в производство";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(620, 430);

            Label orderLabel = new Label { Location = new Point(12, 12), Size = new Size(596, 18), Text = "Папка заказа:" };
            _order.Location = new Point(12, 32);
            _order.Size = new Size(500, 24);
            _order.Text = orderFolder ?? "";
            Button browse = new Button { Text = "Обзор…", Location = new Point(520, 31), Size = new Size(88, 26) };
            browse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Папка заказа";
                    if (Directory.Exists(Order)) dialog.SelectedPath = Order;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    _order.Text = dialog.SelectedPath;
                    Fill();
                }
            };

            Label numberLabel = new Label { Location = new Point(12, 64), Size = new Size(200, 18), Text = "№ заявки:" };
            _number.Location = new Point(12, 84);
            _number.Size = new Size(200, 24);

            Label productsLabel = new Label { Location = new Point(12, 116), Size = new Size(596, 18), Text = "Изделия заказа (галочка — выдаём; тираж правится двойным щелчком):" };
            _products.Location = new Point(12, 136);
            _products.Size = new Size(596, 200);
            _products.View = View.Details;
            _products.CheckBoxes = true;
            _products.FullRowSelect = true;
            _products.Columns.Add("Изделие", 400);
            _products.Columns.Add("Кол-во", 80);
            _products.Columns.Add("Ведомость", 100);
            _products.DoubleClick += delegate { Edit(); };

            Label colorLabel = new Label { Location = new Point(12, 344), Size = new Size(300, 18), Text = "Цвет покраски по умолчанию (Т-14в):" };
            _color.Location = new Point(12, 364);
            _color.Size = new Size(300, 24);
            _color.DropDownStyle = ComboBoxStyle.DropDown;
            _color.Items.AddRange(KnownColors);
            _color.SelectedIndex = 0;

            _draft.Location = new Point(330, 366);
            _draft.Size = new Size(280, 20);
            _draft.Text = "Черновик: только книга, в цех не копировать";

            Button ok = new Button { Text = "Выдать", Location = new Point(428, 396), Size = new Size(84, 26), DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "Отмена", Location = new Point(520, 396), Size = new Size(88, 26), DialogResult = DialogResult.Cancel };
            ok.Click += delegate
            {
                if (!Directory.Exists(Order))
                {
                    Warn("Укажите папку заказа.");
                    DialogResult = DialogResult.None;
                    return;
                }
                if (Quantities.Length == 0)
                {
                    Warn("Отметьте хотя бы одно изделие.");
                    DialogResult = DialogResult.None;
                }
            };

            Controls.AddRange(new Control[] { orderLabel, _order, browse, numberLabel, _number, productsLabel, _products,
                colorLabel, _color, _draft, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Fill();
        }

        /// <summary>Состав и тираж прошлой выдачи подставляются сразу: повторная выдача обычно её повторяет.</summary>
        private void Fill()
        {
            _products.Items.Clear();
            if (!Directory.Exists(Order)) return;
            if (_number.Text.Trim().Length == 0)
            {
                string name = Path.GetFileName(Order.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
                string[] parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                _number.Text = parts.Length > 0 ? parts[0] : name;
            }

            Dictionary<string, int> quantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string previous = Directory.GetFiles(Order, "*_Сводная_заявка*.xlsx").OrderByDescending(f => f).FirstOrDefault();
            if (previous != null) ProdRequestBook.ReadPrevious(previous, colors, quantities);
            foreach (string color in colors.Values.Distinct().Where(c => c.Length > 0 && !_color.Items.Contains(c)))
                _color.Items.Insert(0, color);
            if (colors.Count > 0) _color.SelectedIndex = 0;

            string section = Path.Combine(Order, ProductLocator.SectionFolder);
            if (!Directory.Exists(section)) return;
            foreach (string folder in Directory.GetDirectories(section).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
            {
                string models = Path.Combine(folder, LzkNaming.ModelsFolder);
                if (!Directory.Exists(models)) continue;
                if (!Directory.GetFiles(models, "*.sldasm", SearchOption.AllDirectories)
                        .Any(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))) continue;
                string name = Path.GetFileName(folder);
                bool workbook = Directory.GetFiles(folder, LzkNaming.WorkbookPrefix + "*.xlsx")
                    .Any(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal));
                int quantity;
                if (!quantities.TryGetValue(name, out quantity)) quantity = 1;
                ListViewItem item = new ListViewItem(name) { Checked = true };
                item.SubItems.Add(quantity.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(workbook ? "есть" : "нет");
                _products.Items.Add(item);
            }
        }

        /// <summary>Тираж правится маленьким окном: полноценная сетка здесь только мешала бы.</summary>
        private void Edit()
        {
            ListViewItem item = _products.SelectedItems.Count > 0 ? _products.SelectedItems[0] : null;
            if (item == null) return;
            using (Form dialog = new Form())
            {
                dialog.Text = "Кол-во: " + item.Text;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(220, 90);
                NumericUpDown number = new NumericUpDown
                {
                    Location = new Point(12, 16),
                    Size = new Size(196, 24),
                    Minimum = 1,
                    Maximum = 9999,
                    Value = Math.Max(1, int.Parse(item.SubItems[1].Text, CultureInfo.InvariantCulture))
                };
                Button ok = new Button { Text = "ОК", Location = new Point(124, 52), Size = new Size(84, 26), DialogResult = DialogResult.OK };
                dialog.Controls.AddRange(new Control[] { number, ok });
                dialog.AcceptButton = ok;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                item.SubItems[1].Text = ((int)number.Value).ToString(CultureInfo.InvariantCulture);
            }
        }

        private void Warn(string text)
        {
            MessageBox.Show(this, text, "ЕСКД: выдать в производство", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
