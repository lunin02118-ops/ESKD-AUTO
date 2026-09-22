using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Обозначение детали не совпадает с именем файла (замечание владельца 22.09.2026). Раньше обход изделия только
    /// сообщал об этом, и исправлять приходилось руками в каждой детали. Теперь конструктор отмечает, каким деталям
    /// взять обозначение из имени файла, а каким оставить своё. Отмечены заранее только явные случайности —
    /// имена тел и элементов («Тело5», «Вырез-Вытянуть3[1]»), которые SolidWorks переносит при разделении тел.
    /// </summary>
    public sealed class DesignationPickForm : Form
    {
        private readonly CheckedListBox _list;
        private readonly List<string> _keys = new List<string>();

        /// <summary>Отмеченные детали (ключи, переданные в конструктор).</summary>
        public HashSet<string> Chosen { get; private set; }

        /// <param name="rows">ключ детали → (имя детали, расхождение)</param>
        public DesignationPickForm(IEnumerable<KeyValuePair<string, KeyValuePair<string, DesignationMismatch>>> rows)
        {
            Chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Text = "ЕСКД: обозначение по имени файла";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(760, 380);
            MinimumSize = new Size(520, 260);

            Label head = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(10, 8, 10, 0),
                Text = "Обозначение в свойствах не совпадает с именем файла. Отметьте детали, которым взять обозначение " +
                       "из имени файла; неотмеченные останутся как есть."
            };
            _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
            foreach (var row in rows)
            {
                DesignationMismatch m = row.Value.Value;
                _keys.Add(row.Key);
                _list.Items.Add(string.Format("{0}:  «{1}»  →  «{2}»", row.Value.Key, m.Current, m.Expected),
                    !DesignationParser.LooksLikeDesignation(m.Current));
            }

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6)
            };
            Button later = new Button { Text = "Оставить все", AutoSize = true, DialogResult = DialogResult.Cancel };
            Button ok = new Button { Text = "Исправить отмеченные", AutoSize = true, DialogResult = DialogResult.OK };
            Button all = new Button { Text = "Отметить все", AutoSize = true };
            Button none = new Button { Text = "Снять все", AutoSize = true };
            all.Click += (s, e) => SetAll(true);
            none.Click += (s, e) => SetAll(false);
            buttons.Controls.AddRange(new Control[] { later, ok, none, all });
            AcceptButton = ok;
            CancelButton = later;

            Controls.Add(_list);
            Controls.Add(head);
            Controls.Add(buttons);
            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                foreach (int index in _list.CheckedIndices) Chosen.Add(_keys[index]);
            };
        }

        private void SetAll(bool value)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, value);
        }
    }
}
