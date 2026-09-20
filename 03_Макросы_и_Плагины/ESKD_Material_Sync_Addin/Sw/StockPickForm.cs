using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Выбор материала, когда типоразмеру соответствует несколько записей библиотеки (Р-8, решение владельца
    /// 20.09.2026). Лист 6 мм бывает из Ст3сп и из 09Г2С — выбрать может только конструктор. Один и тот же
    /// типоразмер в разных позициях спрашивается один раз: выбранное применяется ко всем таким позициям.
    ///
    /// Окно показывается из очереди простоя, когда SolidWorks уже отпустил документ, — в обработчике сохранения
    /// модальное окно вешает SolidWorks.
    /// </summary>
    public sealed class StockPickForm : Form
    {
        private sealed class Row
        {
            public string Size = "";
            public string Where = "";
            public List<MaterialInfo> Candidates = new List<MaterialInfo>();
            public ComboBox Box;
        }

        private readonly List<Row> _rows = new List<Row>();

        /// <summary>Что выбрано: типоразмер (нормализованный) → материал. Пусто — конструктор отложил выбор.</summary>
        public Dictionary<string, MaterialInfo> Chosen { get; private set; }

        public StockPickForm(string partTitle, IEnumerable<StockFinding> findings)
        {
            Chosen = new Dictionary<string, MaterialInfo>(StringComparer.Ordinal);
            Group(findings);

            Text = "ЕСКД: материал по типоразмеру";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;

            Label head = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(12, 10, 12, 0),
                Text = string.IsNullOrEmpty(partTitle)
                    ? "Материал не назначен. Типоразмеру соответствует несколько материалов библиотеки — выберите нужный."
                    : partTitle + ": материал не назначен. Типоразмеру соответствует несколько материалов библиотеки — выберите нужный."
            };

            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12, 6, 12, 6),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int line = 0;
            foreach (Row row in _rows)
            {
                Label caption = new Label
                {
                    AutoSize = true,
                    Margin = new Padding(0, 7, 10, 0),
                    Text = row.Size + "  (" + row.Where + ")"
                };
                row.Box = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 3, 0, 3),
                    Width = 560
                };
                foreach (MaterialInfo info in row.Candidates) row.Box.Items.Add(Describe(info));
                row.Box.SelectedIndex = 0;
                table.Controls.Add(caption, 0, line);
                table.Controls.Add(row.Box, 1, line);
                line++;
            }

            Button ok = new Button { Text = "Назначить", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            Button later = new Button { Text = "Позже", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12, 6, 12, 10),
                AutoSize = true
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(later);

            Controls.Add(table);
            Controls.Add(buttons);
            Controls.Add(head);
            AcceptButton = ok;
            CancelButton = later;
            ClientSize = new Size(760, 44 + Math.Max(1, _rows.Count) * 30 + 60);

            FormClosing += delegate
            {
                if (DialogResult != DialogResult.OK) return;
                foreach (Row row in _rows)
                {
                    int at = row.Box.SelectedIndex;
                    if (at >= 0 && at < row.Candidates.Count) Chosen[StockCatalog.NormalizeSize(row.Size)] = row.Candidates[at];
                }
            };
        }

        /// <summary>Один типоразмер — одна строка окна, даже если позиций с ним в детали несколько.</summary>
        private void Group(IEnumerable<StockFinding> findings)
        {
            if (findings == null) return;
            Dictionary<string, Row> byKey = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (StockFinding finding in findings)
            {
                if (!finding.NeedsChoice) continue;
                string key = StockCatalog.NormalizeSize(finding.Request.Size) + "|" + StockCatalog.NormalizeGost(finding.Request.Gost);
                Row row;
                if (byKey.TryGetValue(key, out row))
                {
                    if (row.Where.IndexOf(finding.Folder, StringComparison.Ordinal) < 0) row.Where += ", " + finding.Folder;
                    continue;
                }
                row = new Row
                {
                    Size = finding.Request.Size,
                    Where = finding.Folder,
                    Candidates = new List<MaterialInfo>(finding.Match.Candidates)
                };
                byKey.Add(key, row);
                _rows.Add(row);
            }
        }

        /// <summary>Строка списка: то, что уедет в графу 3, а не имя файла библиотеки.</summary>
        public static string Describe(MaterialInfo info)
        {
            if (info == null) return "";
            string line = (info.LineDesignation ?? "").Trim();
            if (line.Length > 0) return line;
            return (info.Name ?? "").Trim();
        }
    }
}
