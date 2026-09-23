using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using Environment = System.Environment;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Кнопка окна «Замечания» сверх «Копировать» и «Закрыть»: например, «Открыть книгу ЛЗК».</summary>
    public sealed class NoticeButton
    {
        public string Caption = "";
        public Action Run;

        public NoticeButton(string caption, Action run)
        {
            Caption = caption;
            Run = run;
        }
    }

    /// <summary>
    /// Окно «Замечания» — одно на все кнопки вкладки ЕСКД (решение владельца 18.09.2026): вместо текстовых отчётов
    /// и длинных сообщений — таблица с цветной критичностью (критично, замечание, к сведению), отбором по уровню,
    /// колонкой «Что сделать» и полным текстом выделенной строки внизу.
    /// </summary>
    public sealed class NoticeForm : Form
    {
        private static readonly Color CriticalBack = Color.FromArgb(253, 226, 225);
        private static readonly Color WarningBack = Color.FromArgb(255, 243, 205);
        private static readonly Color InfoBack = Color.FromArgb(227, 238, 251);
        private static readonly Color OkBack = Color.FromArgb(221, 244, 224);
        private static readonly Color CriticalFore = Color.FromArgb(176, 0, 32);
        private static readonly Color WarningFore = Color.FromArgb(138, 90, 0);
        private static readonly Color InfoFore = Color.FromArgb(21, 88, 160);
        private static readonly Color OkFore = Color.FromArgb(22, 110, 40);

        private readonly List<Notice> _notices;
        private readonly ListView _list = new ListView();
        private readonly TextBox _detail = new TextBox();
        private readonly Dictionary<NoticeLevel, CheckBox> _filters = new Dictionary<NoticeLevel, CheckBox>();

        /// <param name="title">Заголовок окна, например «ЕСКД: проверка изделия».</param>
        /// <param name="headline">Итог крупно: «ЗАМЕЧАНИЯ», «Книга ЛЗК сохранена».</param>
        /// <param name="details">Пояснение под итогом: изделие, файл, числа.</param>
        /// <param name="headlineLevel">Цвет итога; null — по самому серьёзному замечанию (нет замечаний — зелёный).</param>
        public NoticeForm(string title, string headline, string details, IEnumerable<Notice> notices,
            NoticeLevel? headlineLevel, IList<NoticeButton> buttons)
        {
            _notices = Notices.Ordered(notices);
            Text = title;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(1040, _notices.Count == 0 ? 260 : 600);
            MinimumSize = new Size(640, 260);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            Color back, fore;
            Colors(headlineLevel, out back, out fore);

            Panel head = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = back, Padding = new Padding(14, 10, 14, 6) };
            Label headLabel = new Label
            {
                Text = headline,
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Segoe UI Semibold", 13f),
                ForeColor = fore,
                AutoEllipsis = true
            };
            Label detailLabel = new Label { Text = details ?? "", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(40, 40, 40) };
            head.Controls.Add(detailLabel);
            head.Controls.Add(headLabel);

            FlowLayoutPanel filters = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                Padding = new Padding(10, 6, 10, 0),
                WrapContents = false
            };
            filters.Controls.Add(new Label { Text = "Показывать:", AutoSize = true, Margin = new Padding(4, 7, 8, 0) });
            foreach (NoticeLevel level in new[] { NoticeLevel.Critical, NoticeLevel.Warning, NoticeLevel.Info })
            {
                Color b, f;
                Colors(level, out b, out f);
                CheckBox box = new CheckBox
                {
                    Text = "● " + Caption(level) + ": " + Notices.Count(_notices, level),
                    Checked = true,
                    AutoSize = true,
                    BackColor = b,
                    ForeColor = f,
                    Font = new Font("Segoe UI Semibold", 9f),
                    Padding = new Padding(6, 3, 8, 3),
                    Margin = new Padding(0, 0, 10, 0),
                    Enabled = Notices.Count(_notices, level) > 0
                };
                box.CheckedChanged += (s, e) => Fill();
                _filters[level] = box;
                filters.Controls.Add(box);
            }

            _list.Dock = DockStyle.Fill;
            _list.View = System.Windows.Forms.View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = true;
            _list.ShowItemToolTips = true;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.Columns.Add("Уровень", 120);
            _list.Columns.Add("Документ", 250);
            _list.Columns.Add("Что не так", 380);
            _list.Columns.Add("Что сделать", 260);
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) Copy(Selected());
                if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem item in _list.Items) item.Selected = true;
            };

            _detail.Dock = DockStyle.Bottom;
            _detail.Height = 70;
            _detail.Multiline = true;
            _detail.ReadOnly = true;
            _detail.ScrollBars = ScrollBars.Vertical;
            _detail.BackColor = SystemColors.Window;

            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 8, 8, 4)
            };
            Button close = new Button { Text = "Закрыть", Width = 110, Height = 28, DialogResult = DialogResult.OK };
            bottom.Controls.Add(close);
            Button copy = new Button { Text = "Копировать список", Width = 150, Height = 28, Enabled = _notices.Count > 0 };
            copy.Click += (s, e) => Copy(Filtered(_notices));
            bottom.Controls.Add(copy);
            foreach (NoticeButton b in buttons ?? new NoticeButton[0])
            {
                NoticeButton action = b;
                Button button = new Button { Text = action.Caption, AutoSize = true, Height = 28, MinimumSize = new Size(110, 28) };
                button.Click += (s, e) =>
                {
                    try
                    {
                        if (action.Run != null) action.Run();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Окно замечаний: " + action.Caption, ex);
                        MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                bottom.Controls.Add(button);
            }
            AcceptButton = close;
            CancelButton = close;

            if (_notices.Count == 0)
            {
                Label none = new Label
                {
                    Text = "Замечаний нет.",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 11f),
                    ForeColor = OkFore
                };
                Controls.Add(none);
            }
            else
            {
                Controls.Add(_list);
                Controls.Add(_detail);
                Controls.Add(filters);
            }
            Controls.Add(head);
            Controls.Add(bottom);
            Fill();
            Resize += (s, e) => Stretch();
            Stretch();
        }

        /// <summary>Показать окно поверх SolidWorks. Ничего не возвращает: окно только сообщает.</summary>
        public static void Present(ISldWorks app, string title, string headline, string details, IEnumerable<Notice> notices,
            NoticeLevel? headlineLevel, params NoticeButton[] buttons)
        {
            List<Notice> list = (notices ?? Enumerable.Empty<Notice>()).ToList();
            foreach (Notice n in list) Log.Info(title + ": " + n);
            using (NoticeForm form = new NoticeForm(title, headline, details, list, headlineLevel, buttons))
                form.ShowDialog(Owner(app));
        }

        /// <summary>Окно SolidWorks как владелец модального окна; null — не найдено.</summary>
        internal static IWin32Window Owner(ISldWorks app)
        {
            try
            {
                Frame frame = app != null ? app.Frame() as Frame : null;
                return frame != null ? new WindowWrapper(new IntPtr(frame.GetHWnd())) : null;
            }
            catch (Exception ex)
            {
                Log.Error("Окно замечаний: окно SolidWorks", ex);
                return null;
            }
        }

        private void Colors(NoticeLevel? level, out Color back, out Color fore)
        {
            NoticeLevel effective = level ?? Notices.Max(_notices);
            // Итог без указанного уровня и без критичных и замечаний — зелёный: сведения работе не мешают.
            if (!level.HasValue && effective == NoticeLevel.Info)
            {
                back = OkBack;
                fore = OkFore;
                return;
            }
            back = effective == NoticeLevel.Critical ? CriticalBack : effective == NoticeLevel.Warning ? WarningBack : InfoBack;
            fore = effective == NoticeLevel.Critical ? CriticalFore : effective == NoticeLevel.Warning ? WarningFore : InfoFore;
        }

        private static string Caption(NoticeLevel level)
        {
            return level == NoticeLevel.Critical ? "Критично" : level == NoticeLevel.Warning ? "Замечания" : "К сведению";
        }

        private List<Notice> Filtered(IEnumerable<Notice> notices)
        {
            return notices.Where(n => !_filters.ContainsKey(n.Level) || _filters[n.Level].Checked).ToList();
        }

        private void Fill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (Notice n in Filtered(_notices))
            {
                Color back, fore;
                Colors(n.Level, out back, out fore);
                ListViewItem item = new ListViewItem("● " + Notices.LevelName(n.Level)) { BackColor = back, Tag = n, UseItemStyleForSubItems = false };
                item.ForeColor = fore;
                item.Font = new Font(_list.Font, FontStyle.Bold);
                item.SubItems.Add(n.Document.Length > 0 ? n.Document : "изделие").BackColor = back;
                item.SubItems.Add(n.Text).BackColor = back;
                item.SubItems.Add(n.Hint).BackColor = back;
                item.ToolTipText = n.Text + (n.Hint.Length > 0 ? Environment.NewLine + "→ " + n.Hint : "");
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            ShowDetail();
        }

        private List<Notice> Selected()
        {
            return _list.SelectedItems.Cast<ListViewItem>().Select(i => (Notice)i.Tag).ToList();
        }

        private void ShowDetail()
        {
            Notice n = Selected().FirstOrDefault();
            _detail.Text = n == null ? "" :
                Notices.LevelName(n.Level) + " — " + (n.Document.Length > 0 ? n.Document : "изделие") + Environment.NewLine +
                n.Text + (n.Hint.Length > 0 ? Environment.NewLine + "Что сделать: " + n.Hint : "");
        }

        /// <summary>Колонки «Что не так» и «Что сделать» делят свободную ширину окна.</summary>
        private void Stretch()
        {
            if (_list.Columns.Count < 4) return;
            int free = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 4;
            if (free < 300) return;
            _list.Columns[2].Width = free * 60 / 100;
            _list.Columns[3].Width = free - _list.Columns[2].Width;
        }

        private void Copy(IEnumerable<Notice> notices)
        {
            string text = Notices.Text(notices);
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Error("Окно замечаний: буфер обмена", ex);
            }
        }
    }
}
