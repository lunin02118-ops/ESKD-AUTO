using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.sldworks;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>Ответ на вопрос «изделие изменено после проверки».</summary>
    public enum FreshnessChoice
    {
        CheckNow = 0,
        ContinueAsIs = 1,
        Cancel = 2
    }

    /// <summary>
    /// Изделие проверено и с тех пор не менялось? (решение владельца 23.09.2026, журнал З-27). ЛЗК и выгрузка сверяют
    /// суммы файлов изделия с отчётом проверки `_Проверка.txt` и смотрят, нет ли несохранённых правок. Всё совпало —
    /// кнопка ничего не спрашивает и отмечает в книге (отчёте выгрузки) версию изделия. Нет — один вопрос: «Проверить
    /// сейчас» (окно проверки изделия, потом кнопка продолжает) или «Продолжить как есть» (отметка «не проверено»:
    /// «Готово к производству» такую книгу или выгрузку не примет).
    /// </summary>
    public sealed class ProductFreshness
    {
        /// <summary>Папка изделия — та же, где проверка пишет свой отчёт.</summary>
        public string ProductFolder = "";
        /// <summary>Версия проверки, от которой изделие не менялось; пусто — не проверено или изменено после проверки.</summary>
        public string Version = "";
        /// <summary>Отчёта проверки нет совсем.</summary>
        public bool NeverChecked;
        /// <summary>Почему изделие не считается проверенным — строками для окна и журнала.</summary>
        public readonly List<string> Reasons = new List<string>();
        /// <summary>
        /// Файлы изделия ровно как при проверке — мешают только несохранённые правки. Проверка их не сохраняет, поэтому
        /// «Проверить сейчас» тут не поможет: сохранить (или закрыть без сохранения) может только конструктор.
        /// </summary>
        public bool OnlyUnsaved;
        /// <summary>
        /// Можно предложить «Проверить сейчас». Нельзя, если последняя проверка — другой сборки, а эта сборка не главная:
        /// её проверка заменила бы отчёт главной сборки, и книга с выгрузкой главной сборки «устарели» бы без причины.
        /// </summary>
        public bool CanCheck = true;

        public bool Fresh { get { return Version.Length > 0; } }

        /// <summary>Сверка открытой главной сборки с последней проверкой. Ничего не меняет.</summary>
        public static ProductFreshness Of(ISldWorks app, ModelDoc2 assembly)
        {
            ProductFreshness f = new ProductFreshness();
            string path = DocInfo.PathOf(assembly);
            f.ProductFolder = ProductReviewService.ProductFolderOf(path);
            ProductStamp stamp = ProductStamp.Read(CheckRules.ReportPath(f.ProductFolder));
            if (stamp == null)
            {
                f.NeverChecked = true;
                f.Reasons.Add("изделие ещё не проверялось: отчёта " + CheckRules.ReportName + " нет");
                return f;
            }
            if (stamp.Version.Length == 0)
            {
                f.Reasons.Add("последняя проверка сделана до 23.09.2026 — в её отчёте нет версии изделия");
                return f;
            }
            if (!string.Equals(stamp.Assembly, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                f.Reasons.Add("последняя проверка в папке изделия — другой сборки («" + stamp.Assembly + "»)");
                f.CanCheck = ProductLocator.IsMainAssembly(path);
                return f;
            }
            // Флаг сборки — до обращения к составу, как в проверке.
            bool topEdited = DocumentGuard.HasUserEdits(assembly);
            List<ProductNode> nodes = ProductReviewService.Collect(app, assembly, path, f.ProductFolder, null);
            nodes[0].Edited = topEdited;
            List<string> edited = nodes.Where(n => n.Edited && Own(n)).Select(n => Path.GetFileName(n.Path)).ToList();
            List<KeyValuePair<string, string>> now = nodes
                .Select(n => new KeyValuePair<string, string>(Path.GetFileName(n.Path), Checksum(n.Path))).ToList();
            List<string> changed = ProductStamp.Differences(stamp.Checksums, now);
            if (edited.Count > 0) f.Reasons.Add("есть несохранённые правки: " + Names(edited));
            if (changed.Count > 0) f.Reasons.Add("изменены после проверки (или добавлены, убраны): " + Names(changed));
            f.OnlyUnsaved = edited.Count > 0 && changed.Count == 0;
            if (f.Reasons.Count == 0) f.Version = stamp.Version;
            return f;
        }

        /// <summary>
        /// Свой документ изделия — главная сборка и непокупные модели из папки изделия. Несохранённые правки считаются только
        /// в них: чужие файлы (база, покупные, другие папки заказа) SolidWorks помечает изменёнными сам — при открытии
        /// файла старой версии, после замера ЛЗК, — и из-за них проверка никогда бы не сошлась.
        /// </summary>
        public static bool Own(ProductNode node)
        {
            return node.IsTop || (node.InProduct && !node.IsPurchased);
        }

        /// <summary>
        /// Перед ЛЗК и выгрузкой: проверено и не менялось — сразу дальше; нет — вопрос (без окна — «как есть», причина в
        /// журнале). «Проверить сейчас» открывает окно проверки изделия, после него изделие сверяется ещё раз.
        /// cancelled — конструктор отказался от кнопки.
        /// </summary>
        /// <param name="step">Кнопка — «Ведомость ЛЗК», «Выгрузка для производства».</param>
        /// <param name="unchecked">Что будет при «Продолжить как есть» — строка для окна.</param>
        public static ProductFreshness Ensure(ISldWorks app, ModelDoc2 assembly, string step, string @unchecked, bool interactive,
            out bool cancelled)
        {
            cancelled = false;
            ProductFreshness f = Measure(app, assembly, step);
            if (f.Fresh) return f;
            Log.Info(step + ": изделие не проверено или изменено после проверки — " + string.Join("; ", f.Reasons.ToArray()));
            if (!interactive) return f;
            bool afterCheck = false;
            while (true)
            {
                // «Проверить сейчас» — один раз: не сошлось и после проверки (окно закрыли без записи, правки не сохранены) —
                // вопрос повторяется без неё, молча «как есть» кнопка не продолжает.
                bool offerCheck = !afterCheck && f.CanCheck && !f.OnlyUnsaved;
                FreshnessChoice choice;
                using (FreshnessForm form = new FreshnessForm(step, f, @unchecked, offerCheck, afterCheck))
                {
                    form.ShowDialog(NoticeForm.Owner(app));
                    choice = form.Choice;
                }
                if (choice == FreshnessChoice.Cancel)
                {
                    cancelled = true;
                    return f;
                }
                if (choice == FreshnessChoice.ContinueAsIs) return f;
                CheckService.Run(app, CheckMode.Interactive);
                afterCheck = true;
                f = Measure(app, assembly, step);
                if (f.Fresh) return f;
                Log.Info(step + ": после проверки изделие всё ещё не сходится с ней — " + string.Join("; ", f.Reasons.ToArray()));
            }
        }

        private static ProductFreshness Measure(ISldWorks app, ModelDoc2 assembly, string step)
        {
            Status(app, "ЕСКД: " + step + " — сверка с проверкой изделия…");
            ProductFreshness f = Of(app, assembly);
            Status(app, "");
            return f;
        }

        /// <summary>
        /// Новые суммы файлов, которые сохранила кнопка, — в отчёт проверки (<see cref="ProductStamp.Restamp"/>): иначе
        /// следующая кнопка сочла бы изделие изменённым после проверки, хотя конструктор ничего не менял.
        /// </summary>
        public static void Restamp(string productFolder, IList<StampChange> changes, string step)
        {
            if (changes == null || changes.Count == 0) return;
            string path = CheckRules.ReportPath(productFolder);
            try
            {
                if (!File.Exists(path)) return;
                int replaced;
                string text = ProductStamp.Restamp(File.ReadAllText(path, Encoding.UTF8), changes, step, DateTime.Now, out replaced);
                if (replaced == 0) return;
                File.WriteAllText(path, text, new UTF8Encoding(true));
                Log.Info(step + ": в отчёте проверки обновлены суммы файлов, сохранённых кнопкой — " + replaced);
            }
            catch (IOException ex)
            {
                Log.Error(step + ": суммы в отчёте проверки", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error(step + ": суммы в отчёте проверки", ex);
            }
        }

        /// <summary>SHA-256 файла шестнадцатеричной строкой; не прочитался — пусто.</summary>
        public static string Checksum(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch (IOException ex)
            {
                Log.Error("Контрольная сумма " + path, ex);
                return "";
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("Контрольная сумма " + path, ex);
                return "";
            }
        }

        private static string Names(List<string> names)
        {
            const int shown = 5;
            string list = string.Join(", ", names.Take(shown).ToArray());
            return names.Count > shown ? list + " и ещё " + (names.Count - shown) : list;
        }

        private static void Status(ISldWorks app, string text)
        {
            try
            {
                Frame frame = app.Frame() as Frame;
                if (frame != null) frame.SetStatusBarText(text);
            }
            catch (COMException ex)
            {
                Log.Error("Сверка с проверкой: строка состояния", ex);
            }
        }
    }

    /// <summary>Вопрос «изделие изменено после проверки»: «Проверить сейчас» / «Продолжить как есть» / «Отмена».</summary>
    internal sealed class FreshnessForm : Form
    {
        public FreshnessChoice Choice { get; private set; }

        public FreshnessForm(string step, ProductFreshness freshness, string @unchecked, bool offerCheck, bool afterCheck)
        {
            Choice = FreshnessChoice.Cancel;
            Text = "ЕСКД: " + step;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 300);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            Panel head = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(255, 244, 214), Padding = new Padding(14, 12, 14, 6) };
            head.Controls.Add(new Label
            {
                Text = afterCheck ? "Изделие всё ещё не сходится с проверкой"
                    : freshness.OnlyUnsaved ? "В изделии несохранённые правки"
                    : freshness.NeverChecked ? "Изделие ещё не проверялось" : "Изделие изменилось после проверки",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 13f),
                ForeColor = Color.FromArgb(120, 72, 0),
                AutoEllipsis = true
            });

            StringBuilder body = new StringBuilder();
            foreach (string reason in freshness.Reasons) body.AppendLine("• " + reason);
            body.AppendLine();
            if (offerCheck)
                body.AppendLine("«Проверить сейчас» — окно «Проверить изделие»: ответьте на вопросы, сохраните, и «" + step +
                    "» продолжится сама.");
            else if (freshness.OnlyUnsaved)
                body.AppendLine("Проверка изделия несохранённые правки не сохраняет. Сохраните эти документы (или закройте их без " +
                    "сохранения) и нажмите «" + step + "» снова.");
            else if (!freshness.CanCheck)
                body.AppendLine("Изделие проверяют на главной сборке: проверка этой сборки заменила бы её отчёт. Откройте главную " +
                    "сборку и нажмите «Проверить изделие».");
            else if (afterCheck)
                body.AppendLine("Проверку закрыли без записи, или в изделии остались несохранённые правки. Сохраните их и " +
                    "нажмите «" + step + "» снова.");
            body.AppendLine("«Продолжить как есть» — " + @unchecked);
            TextBox text = new TextBox
            {
                Text = body.ToString().TrimEnd(),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                TabStop = false
            };
            Panel middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 6), BackColor = SystemColors.Window };
            middle.Controls.Add(text);

            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 8, 10, 8)
            };
            Button cancel = new Button { Text = "Отмена", Width = 100, Height = 28, DialogResult = DialogResult.Cancel };
            Button asIs = new Button { Text = "Продолжить как есть", AutoSize = true, Height = 28, MinimumSize = new Size(150, 28) };
            Button check = new Button { Text = "Проверить сейчас", AutoSize = true, Height = 28, MinimumSize = new Size(150, 28) };
            asIs.Click += (s, e) => Done(FreshnessChoice.ContinueAsIs);
            check.Click += (s, e) => Done(FreshnessChoice.CheckNow);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(asIs);
            if (offerCheck)
            {
                bottom.Controls.Add(check);
                AcceptButton = check;
            }
            CancelButton = cancel;

            Controls.Add(middle);
            Controls.Add(head);
            Controls.Add(bottom);
            ActiveControl = offerCheck ? check : cancel;
        }

        private void Done(FreshnessChoice choice)
        {
            Choice = choice;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
