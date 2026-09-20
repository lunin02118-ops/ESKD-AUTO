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

            /// <summary>Где этот типоразмер встретился: детали при обходе изделия, папки списка вырезов — в одной детали.</summary>
            public readonly List<string> Places = new List<string>();
            public int More;

            public List<MaterialInfo> Candidates = new List<MaterialInfo>();
            public ComboBox Box;

            public string Where
            {
                get
                {
                    string line = string.Join(", ", Places.ToArray());
                    return More > 0 ? line + " и ещё " + More : line;
                }
            }
        }

        private readonly List<Row> _rows = new List<Row>();
        private Button _ok;

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
                // Ничего не выбрано заранее: вопрос, у которого уже есть ответ, — не вопрос. Иначе «Назначить»,
                // нажатая не глядя, поставила бы первую по алфавиту марку (у листа 6 мм это 09Г2С, а не Ст3сп).
                row.Box.SelectedIndexChanged += delegate { UpdateOk(); };
                table.Controls.Add(caption, 0, line);
                table.Controls.Add(row.Box, 1, line);
                line++;
            }

            _ok = new Button { Text = "Назначить", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(6, 0, 0, 0), Enabled = false };
            Button ok = _ok;
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
            // Ширина по содержимому: при обходе изделия слева стоят имена деталей, и в 760 точек они не влезают.
            int captions = 0;
            foreach (Row row in _rows)
                captions = Math.Max(captions, TextRenderer.MeasureText(row.Size + "  (" + row.Where + ")", Font).Width);
            ClientSize = new Size(Math.Min(1100, Math.Max(760, captions + 600)), 44 + Math.Max(1, _rows.Count) * 30 + 60);

            FormClosing += delegate
            {
                if (DialogResult != DialogResult.OK) return;
                ReadChoices();
            };
        }

        /// <summary>«Назначить» доступна, только когда выбрана каждая строка: половина выбора хуже, чем никакого.</summary>
        public void UpdateOk()
        {
            if (_ok == null) return;
            foreach (Row row in _rows)
            {
                if (row.Box.SelectedIndex < 0) { _ok.Enabled = false; return; }
            }
            _ok.Enabled = _rows.Count > 0;
        }

        /// <summary>
        /// Снять выбранное из списков. Вызывается при закрытии окна кнопкой «Назначить»; отдельным методом —
        /// чтобы выбор конструктора проверялся юнит-тестом, а не только руками на живом SolidWorks.
        /// </summary>
        public void ReadChoices()
        {
            foreach (Row row in _rows)
            {
                int at = row.Box.SelectedIndex;
                if (at >= 0 && at < row.Candidates.Count) Chosen[StockCatalog.NormalizeSize(row.Size)] = row.Candidates[at];
            }
        }

        /// <summary>Подписи строк окна: типоразмер и где он встретился. Для проверки состава без показа окна.</summary>
        public string[] Captions()
        {
            List<string> out_ = new List<string>();
            foreach (Row row in _rows) out_.Add(row.Size + "  (" + row.Where + ")");
            return out_.ToArray();
        }

        /// <summary>Списки окна — по одному на типоразмер, в порядке строк. Для проверки состава без показа окна.</summary>
        public ComboBox[] Lists()
        {
            List<ComboBox> boxes = new List<ComboBox>();
            foreach (Row row in _rows) boxes.Add(row.Box);
            return boxes.ToArray();
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
                string where = Where(finding);
                Row row;
                if (byKey.TryGetValue(key, out row))
                {
                    if (row.Places.Count < 4 && !row.Places.Contains(where)) row.Places.Add(where);
                    else if (!row.Places.Contains(where)) row.More++;
                    continue;
                }
                row = new Row
                {
                    Size = finding.Request.Size,
                    Candidates = new List<MaterialInfo>(finding.Match.Candidates)
                };
                row.Places.Add(where);
                byKey.Add(key, row);
                _rows.Add(row);
            }
        }

        /// <summary>
        /// Где встретился типоразмер. При обходе изделия это деталь: имён папок списка вырезов там по десятку
        /// одинаковых, и «Элемент списка вырезов2» конструктору ничего не говорит. В окне про одну деталь
        /// её имя уже стоит в заголовке, поэтому показывается папка.
        /// </summary>
        private static string Where(StockFinding finding)
        {
            string owner = (finding.Owner ?? "").Trim();
            return owner.Length > 0 ? owner : (finding.Folder ?? "").Trim();
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
