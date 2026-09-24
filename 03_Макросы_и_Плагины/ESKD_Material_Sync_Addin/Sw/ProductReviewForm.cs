using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using Environment = System.Environment;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Чем закрыто окно «Проверить изделие».</summary>
    public enum ReviewChoice
    {
        Cancel = 0,
        /// <summary>Записать в открытые документы, ничего не сохраняя.</summary>
        Apply = 1,
        /// <summary>Записать и сохранить отмеченные файлы — это и есть команда конструктора сохранить.</summary>
        ApplyAndSave = 2
    }

    /// <summary>
    /// Окно «Проверить изделие» (решение владельца 23.09.2026, журнал З-27): все вопросы изделия в одном месте и одна
    /// кнопка, которая закрывает их полностью — без повторного Ctrl+S в каждой детали. Сверху вниз:
    ///   «Вопросы» — материал к профилю, обозначение не по имени файла; «Решить позже» — ничего не меняется;
    ///   «Обновится само» — имена, дробь, масса, формат, материал с одним подходящим;
    ///   «Замечания» — то, что окно не исправляет (нет чертежа, книги ЛЗК, выгрузки…);
    ///   «Сохранить» — файлы, которые изменятся; с несохранёнными правками конструктора — без галочки.
    /// </summary>
    public sealed class ProductReviewForm : Form
    {
        public const string Later = "Решить позже — ничего не менять";

        private static readonly Color CriticalBack = Color.FromArgb(253, 226, 225);
        private static readonly Color WarningBack = Color.FromArgb(255, 243, 205);
        private static readonly Color InfoBack = Color.FromArgb(227, 238, 251);
        private static readonly Color CriticalFore = Color.FromArgb(176, 0, 32);
        private static readonly Color WarningFore = Color.FromArgb(138, 90, 0);
        private static readonly Color InfoFore = Color.FromArgb(21, 88, 160);

        private readonly ReviewPlan _plan;
        private readonly List<ReviewQuestion> _questions;
        private readonly List<ComboBox> _answers = new List<ComboBox>();
        private readonly List<ReviewFile> _files;
        private readonly bool[] _checked;
        private readonly CheckedListBox _save = new CheckedListBox();
        private readonly TextBox _detail = new TextBox();
        private readonly Button _applySave;
        private readonly Button _apply;
        private readonly ToolTip _tips = new ToolTip();

        public ReviewChoice Choice { get; private set; }

        /// <param name="findings">Замечания проверки, которые окно не исправляет.</param>
        public ProductReviewForm(ReviewPlan plan, IEnumerable<Notice> findings, string headline, string details)
        {
            _plan = plan ?? new ReviewPlan();
            _questions = _plan.Questions.ToList();
            _files = _plan.ListedFiles();
            _checked = _files.Select(f => f.Save).ToArray();
            List<Notice> notices = Notices.Ordered(findings);
            Choice = ReviewChoice.Cancel;

            Text = "ЕСКД: проверка изделия";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(1100, 760);
            MinimumSize = new Size(760, 480);

            NoticeLevel level = notices.Count > 0 ? Notices.Max(notices) : NoticeLevel.Info;
            if (level == NoticeLevel.Info && _questions.Count > 0) level = NoticeLevel.Warning;
            Panel head = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Back(level), Padding = new Padding(14, 8, 14, 4) };
            head.Controls.Add(new Label { Text = details ?? "", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(40, 40, 40) });
            head.Controls.Add(new Label
            {
                Text = headline ?? "",
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Segoe UI Semibold", 13f),
                ForeColor = Fore(level),
                AutoEllipsis = true
            });

            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 6, 10, 0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            List<KeyValuePair<Control, float>> sections = new List<KeyValuePair<Control, float>>();
            if (_questions.Count > 0)
                sections.Add(Section("Вопросы — выберите ответ (" + _questions.Count + ")", QuestionList(), 36f));
            List<ReviewFile> changing = _files.Where(f => f.Changes.Count > 0).ToList();
            if (changing.Count > 0)
                sections.Add(Section("Обновится само — изменений " + _plan.AutoChanges + " в файлах: " + changing.Count, ChangeList(changing), 22f));
            if (notices.Count > 0)
                sections.Add(Section("Замечания — это окно их не исправляет (" + notices.Count + ")", FindingList(notices), 22f));
            if (_files.Count > 0)
                sections.Add(Section("Сохранить после применения", SavePanel(), 20f));
            body.RowCount = sections.Count;
            for (int i = 0; i < sections.Count; i++)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Percent, sections[i].Value));
                body.Controls.Add(sections[i].Key, 0, i);
            }

            _detail.Dock = DockStyle.Bottom;
            _detail.Height = 64;
            _detail.Multiline = true;
            _detail.ReadOnly = true;
            _detail.ScrollBars = ScrollBars.Vertical;
            _detail.BackColor = SystemColors.Window;

            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 8, 8, 4)
            };
            _applySave = new Button { AutoSize = true, Height = 30, MinimumSize = new Size(200, 30), Font = new Font("Segoe UI Semibold", 9f) };
            _applySave.Click += (s, e) => Finish(ReviewChoice.ApplyAndSave);
            _apply = new Button { Text = "Применить без сохранения", AutoSize = true, Height = 30, MinimumSize = new Size(180, 30) };
            _apply.Click += (s, e) => Finish(ReviewChoice.Apply);
            Button cancel = new Button { Text = "Отмена", Width = 110, Height = 30, DialogResult = DialogResult.Cancel };
            bottom.Controls.Add(_applySave);
            bottom.Controls.Add(_apply);
            bottom.Controls.Add(cancel);
            CancelButton = cancel;
            _tips.SetToolTip(_applySave, "Записать ответы и обновления и сохранить отмеченные файлы");
            _tips.SetToolTip(_apply, "Записать в открытые документы; сохраните их сами");
            _tips.SetToolTip(cancel, "Ничего не менять");

            Controls.Add(body);
            Controls.Add(_detail);
            Controls.Add(head);
            Controls.Add(bottom);
            UpdateButtons();
        }

        // ------------------------------------------------------------------ для проверки без показа окна
        /// <summary>Списки ответов по порядку вопросов плана. Первый пункт каждого — <see cref="Later"/>.</summary>
        public ComboBox[] AnswerLists()
        {
            return _answers.ToArray();
        }

        public CheckedListBox SaveList
        {
            get { return _save; }
        }

        public string SaveCaption
        {
            get { return _applySave.Text; }
        }

        public bool CanApplyAndSave
        {
            get { return _applySave.Enabled; }
        }

        public bool CanApply
        {
            get { return _apply.Enabled; }
        }

        /// <summary>Ответы и галочки — в план, выбор — в <see cref="Choice"/>. Кнопки окна зовут это же.</summary>
        public void Commit(ReviewChoice choice)
        {
            Read();
            Choice = choice;
        }

        // ------------------------------------------------------------------ разделы
        private static KeyValuePair<Control, float> Section(string title, Control content, float weight)
        {
            GroupBox box = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 9f),
                Padding = new Padding(6, 4, 6, 6)
            };
            content.Font = new Font("Segoe UI", 9f);
            content.Dock = DockStyle.Fill;
            box.Controls.Add(content);
            return new KeyValuePair<Control, float>(box, weight);
        }

        private Control QuestionList()
        {
            Panel scroll = new Panel { AutoScroll = true };
            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 2, 12, 2)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < _questions.Count; i++)
            {
                ReviewQuestion q = _questions[i];
                Label caption = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(490, 0),
                    Margin = new Padding(0, 5, 10, 5),
                    Text = q.Subject + Environment.NewLine + "    " + (q.Kind == ReviewQuestionKind.Material ? "детали: " : "деталь: ") + q.Where
                };
                ComboBox box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 7, 0, 5) };
                box.Items.Add(Later);
                foreach (string option in q.Options) box.Items.Add(option);
                box.SelectedIndex = q.Answered ? q.Answer + 1 : 0;
                ReviewQuestion question = q;
                box.SelectedIndexChanged += (s, e) =>
                {
                    UpdateButtons();
                    _detail.Text = question.Subject + Environment.NewLine + "Детали: " + string.Join(", ", question.Places.ToArray());
                };
                _tips.SetToolTip(caption, string.Join(Environment.NewLine, question.Places.ToArray()));
                _answers.Add(box);
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(caption, 0, i);
                table.Controls.Add(box, 1, i);
            }
            scroll.Controls.Add(table);
            return scroll;
        }

        private Control ChangeList(List<ReviewFile> files)
        {
            ListView list = NewList();
            list.Columns.Add("Файл", 320);
            list.Columns.Add("Что обновится", 700);
            foreach (ReviewFile f in files)
            {
                const int Shown = 3;
                string summary = string.Join(";  ", f.Changes.Take(Shown).ToArray()) +
                    (f.Changes.Count > Shown ? ";  и ещё " + (f.Changes.Count - Shown) : "");
                ListViewItem item = new ListViewItem(f.FileName) { Tag = f };
                item.SubItems.Add(summary);
                item.ToolTipText = string.Join(Environment.NewLine, f.Changes.ToArray());
                list.Items.Add(item);
            }
            list.SelectedIndexChanged += (s, e) =>
            {
                ReviewFile f = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as ReviewFile : null;
                if (f != null) _detail.Text = f.FileName + ":" + Environment.NewLine + string.Join(Environment.NewLine, f.Changes.ToArray());
            };
            return list;
        }

        private Control FindingList(List<Notice> notices)
        {
            ListView list = NewList();
            list.Columns.Add("Уровень", 120);
            list.Columns.Add("Документ", 260);
            list.Columns.Add("Что не так", 420);
            list.Columns.Add("Что сделать", 260);
            foreach (Notice n in notices)
            {
                ListViewItem item = new ListViewItem("● " + Notices.LevelName(n.Level))
                {
                    Tag = n, BackColor = Back(n.Level), ForeColor = Fore(n.Level), UseItemStyleForSubItems = false
                };
                item.SubItems.Add(n.Document.Length > 0 ? n.Document : "изделие").BackColor = Back(n.Level);
                item.SubItems.Add(n.Text).BackColor = Back(n.Level);
                item.SubItems.Add(n.Hint).BackColor = Back(n.Level);
                item.ToolTipText = n.Text + (n.Hint.Length > 0 ? Environment.NewLine + "→ " + n.Hint : "");
                list.Items.Add(item);
            }
            list.SelectedIndexChanged += (s, e) =>
            {
                Notice n = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Notice : null;
                if (n != null)
                    _detail.Text = Notices.LevelName(n.Level) + " — " + (n.Document.Length > 0 ? n.Document : "изделие") + Environment.NewLine +
                        n.Text + (n.Hint.Length > 0 ? Environment.NewLine + "Что сделать: " + n.Hint : "");
            };
            return list;
        }

        private Control SavePanel()
        {
            Panel panel = new Panel();
            _save.Dock = DockStyle.Fill;
            _save.CheckOnClick = true;
            _save.IntegralHeight = false;
            _save.HorizontalScrollbar = true;
            for (int i = 0; i < _files.Count; i++)
            {
                ReviewFile f = _files[i];
                _save.Items.Add(f.FileName + (f.Edited ? "   — в файле ваши несохранённые правки: галочка снята, сохраните его сами" : ""), _checked[i]);
            }
            _save.ItemCheck += (s, e) =>
            {
                if (e.Index >= 0 && e.Index < _checked.Length) _checked[e.Index] = e.NewValue == CheckState.Checked;
                UpdateButtons();
            };
            Label note = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.FromArgb(70, 70, 70),
                Text = "Отмеченные файлы сохранятся после применения. Без галочки — изменятся, но останутся несохранёнными."
            };
            panel.Controls.Add(_save);
            panel.Controls.Add(note);
            return panel;
        }

        private static ListView NewList()
        {
            return new ListView
            {
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                ShowItemToolTips = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
        }

        // ------------------------------------------------------------------ состояние
        /// <summary>Выбранное в списках — в план: ответ «Решить позже» — без ответа.</summary>
        private void Read()
        {
            for (int i = 0; i < _answers.Count && i < _questions.Count; i++)
                _questions[i].Answer = _answers[i].SelectedIndex - 1;
            for (int i = 0; i < _files.Count; i++) _files[i].Save = _checked[i];
        }

        private void UpdateButtons()
        {
            if (_applySave == null || _apply == null) return;
            Read();
            int save = _plan.ToSave();
            _applySave.Text = ReviewPlan.SaveCaption(save);
            _applySave.Enabled = save > 0;
            _apply.Enabled = _plan.Files.Any(_plan.WillChange);
        }

        private void Finish(ReviewChoice choice)
        {
            Commit(choice);
            DialogResult = DialogResult.OK;
        }

        private static Color Back(NoticeLevel level)
        {
            return level == NoticeLevel.Critical ? CriticalBack : level == NoticeLevel.Warning ? WarningBack : InfoBack;
        }

        private static Color Fore(NoticeLevel level)
        {
            return level == NoticeLevel.Critical ? CriticalFore : level == NoticeLevel.Warning ? WarningFore : InfoFore;
        }
    }
}
