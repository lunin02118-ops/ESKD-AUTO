using System;
using System.Drawing;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Окно кнопки К-7 «Новая ревизия» (ТЗ-02 Т-49): что меняем, почему, код причины и что делать с заделом.
    /// Номер ревизии не редактируется — он всегда следующий за текущим, иначе журнал перестанет сходиться
    /// со штампами чертежей.
    /// </summary>
    public sealed class RevisionForm : Form
    {
        private readonly TextBox _what = new TextBox();
        private readonly ComboBox _reason = new ComboBox();
        private readonly ComboBox _backlog = new ComboBox();

        /// <summary>Что изменено — текст для журнала.</summary>
        public string What { get { return _what.Text.Trim(); } }
        /// <summary>Код причины по ГОСТ 2.503.</summary>
        public ChangeReason Reason { get { return _reason.SelectedItem as ChangeReason ?? ChangeReasons.All[ChangeReasons.All.Length - 1]; } }
        /// <summary>Задел: использовать / доработать / в брак.</summary>
        public string Backlog { get { return _backlog.Text.Trim(); } }

        public RevisionForm(string document, int current, int next)
        {
            Text = "ЕСКД: новая ревизия";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(560, 330);

            Label head = new Label
            {
                Location = new Point(12, 12),
                Size = new Size(536, 40),
                Text = document + Environment.NewLine +
                       "Ревизия " + (current > 0 ? current.ToString() : "черновик") + "  →  " + next
            };

            Label whatLabel = new Label { Location = new Point(12, 60), Size = new Size(536, 18), Text = "Что изменено:" };
            _what.Location = new Point(12, 80);
            _what.Size = new Size(536, 90);
            _what.Multiline = true;
            _what.ScrollBars = ScrollBars.Vertical;

            Label reasonLabel = new Label { Location = new Point(12, 180), Size = new Size(536, 18), Text = "Причина (ГОСТ 2.503-2013, табл. Б.1):" };
            _reason.Location = new Point(12, 200);
            _reason.Size = new Size(536, 24);
            _reason.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (ChangeReason reason in ChangeReasons.All) _reason.Items.Add(reason);
            _reason.SelectedIndex = 3;  // «устранение ошибок» — самая частая причина правки после выдачи

            Label backlogLabel = new Label { Location = new Point(12, 232), Size = new Size(536, 18), Text = "Задел в производстве:" };
            _backlog.Location = new Point(12, 252);
            _backlog.Size = new Size(260, 24);
            _backlog.DropDownStyle = ComboBoxStyle.DropDownList;
            _backlog.Items.AddRange(new object[] { "использовать", "доработать", "в брак" });
            _backlog.SelectedIndex = 0;

            Button ok = new Button { Text = "Записать", Location = new Point(372, 288), Size = new Size(84, 26), DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "Отмена", Location = new Point(464, 288), Size = new Size(84, 26), DialogResult = DialogResult.Cancel };
            ok.Click += delegate
            {
                if (What.Length != 0) return;
                MessageBox.Show(this, "Напишите, что изменено: эта строка заменяет извещение об изменении.",
                    "ЕСКД: новая ревизия", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            };

            Controls.AddRange(new Control[] { head, whatLabel, _what, reasonLabel, _reason, backlogLabel, _backlog, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
