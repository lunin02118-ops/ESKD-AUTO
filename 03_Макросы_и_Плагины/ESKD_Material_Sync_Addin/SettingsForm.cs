using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ESKD.MaterialSync
{
    public class WindowWrapper : IWin32Window
    {
        private readonly IntPtr _hwnd;
        public WindowWrapper(IntPtr handle) { _hwnd = handle; }
        public IntPtr Handle { get { return _hwnd; } }
    }

    public class SettingsForm : Form
    {
        private const string RegPath = @"Software\SolidWorks\ESKD_Settings";

        /// <summary>
        /// D-12: поиск корня репозитория по маркёрам от каталога сборки вверх:
        /// файл MProp.ini внутри дерева Макросы_SW_ZTool либо сама папка Макросы_SW_ZTool
        /// (в корне репо или внутри 03_Макросы_и_Плагины).
        /// </summary>
        private static List<string> FindRepoRootsByMarker()
        {
            List<string> roots = new List<string>();
            try
            {
                string cur = Path.GetDirectoryName(typeof(SettingsForm).Assembly.Location);
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
                {
                    bool markerFound = false;

                    // Маркёр 1: MProp.ini из комплекта SWPlus внутри Макросы_SW_ZTool
                    string markerIni = Path.Combine(cur, "Макросы_SW_ZTool", "SWPlusMacro_v_2018_SP0.0", "MProp", "MProp.ini");
                    if (File.Exists(markerIni)) markerFound = true;

                    // Маркёр 2: папка Макросы_SW_ZTool в текущем каталоге
                    if (!markerFound && Directory.Exists(Path.Combine(cur, "Макросы_SW_ZTool"))) markerFound = true;

                    // Маркёр 3: папка Макросы_SW_ZTool внутри 03_Макросы_и_Плагины
                    if (!markerFound && Directory.Exists(Path.Combine(cur, "03_Макросы_и_Плагины", "Макросы_SW_ZTool"))) markerFound = true;

                    if (markerFound)
                    {
                        bool already = false;
                        foreach (string r in roots)
                        {
                            if (string.Equals(r, cur, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
                        }
                        if (!already) roots.Add(cur);
                    }

                    DirectoryInfo p = Directory.GetParent(cur);
                    cur = p != null ? p.FullName : null;
                }
            }
            catch { }
            return roots;
        }

        private static string[] FindCandidatePaths(string relativeSubPath)
        {
            List<string> list = new List<string>();
            try
            {
                string asmPath = typeof(SettingsForm).Assembly.Location;
                string cur = Path.GetDirectoryName(asmPath);
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(cur); i++)
                {
                    string cand1 = Path.Combine(cur, relativeSubPath);
                    if (File.Exists(cand1)) list.Add(cand1);
                    string cand2 = Path.Combine(cur, @"03_Макросы_и_Плагины", relativeSubPath);
                    if (File.Exists(cand2)) list.Add(cand2);
                    string cand3 = Path.Combine(cur, @"macros\SWPlus_ESKD", Path.GetFileName(Path.GetDirectoryName(relativeSubPath)), Path.GetFileName(relativeSubPath));
                    if (File.Exists(cand3)) list.Add(cand3);
                    DirectoryInfo p = Directory.GetParent(cur);
                    cur = p != null ? p.FullName : null;
                }
            }
            catch { }

            // D-12: fallback от каталога сборки — если жёсткие каталоги не существуют,
            // вычисляем корень репозитория по маркёрам (MProp.ini / папка Макросы_SW_ZTool)
            // и строим относительные подпути от него.
            foreach (string root in FindRepoRootsByMarker())
            {
                try
                {
                    string fb1 = Path.Combine(root, relativeSubPath);
                    if (File.Exists(fb1)) list.Add(fb1);
                    string fb2 = Path.Combine(root, @"03_Макросы_и_Плагины", relativeSubPath);
                    if (File.Exists(fb2)) list.Add(fb2);
                }
                catch { }
            }

            // Primary: жёсткие пути этой станции (используются, когда существуют)
            try
            {
                string primaryDir = @"D:\Work\_Инструменты_Конструктора\03_Макросы_и_Плагины";
                if (Directory.Exists(primaryDir))
                {
                    string cand = Path.Combine(primaryDir, relativeSubPath);
                    if (File.Exists(cand)) list.Add(cand);
                }
                string devDir = @"D:\_dev\solidworks-eskd-suite\macros\SWPlus_ESKD";
                if (Directory.Exists(devDir))
                {
                    string fn = Path.GetFileName(relativeSubPath);
                    string parentDir = Path.GetFileName(Path.GetDirectoryName(relativeSubPath));
                    string cand = Path.Combine(devDir, parentDir, fn);
                    if (File.Exists(cand)) list.Add(cand);
                }
            }
            catch { }

            List<string> res = new List<string>();
            foreach (string s in list)
            {
                if (!File.Exists(s)) continue;
                bool exists = false;
                foreach (string r in res)
                {
                    if (string.Equals(r, s, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) res.Add(s);
            }
            return res.ToArray();
        }

        private static string[] GetSwPlusFamPaths()
        {
            return FindCandidatePaths(@"Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\MProp\MProp_Fam.txt");
        }

        private static string[] GetSwPlusFirmPaths()
        {
            return FindCandidatePaths(@"Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\MProp\MProp_Firm.txt");
        }

        private readonly ISldWorks _swApp;

        private ComboBox cmbAuthor;
        private ComboBox cmbChecker;
        private ComboBox cmbOrg;
        private CheckBox chkServiceEnabled;
        private Label lblServiceStatus;
        private CheckBox chkAutoSyncMaterials;
        private CheckBox chkAutoMass;
        private NumericUpDown numMassDecimals;
        private CheckBox chkAutoCenterMass;
        private CheckBox chkAutoSplitName;
        private Button btnSave;
        private Button btnCancel;
        private Button btnApplyNow;

        public SettingsForm(ISldWorks swApp)
        {
            _swApp = swApp;
            InitializeComponent();
            LoadSettings();
        }

        public SettingsForm() : this(null)
        {
        }

        private void InitializeComponent()
        {
            this.Text = "Настройки ЕСКД — Синхронизация и реквизиты";
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(680, 720);
            this.MinimumSize = new Size(650, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(243, 244, 246); // Modern Light Gray
            this.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            this.MaximizeBox = false;

            try
            {
                string asmLoc = typeof(SettingsForm).Assembly.Location;
                string dir = Path.GetDirectoryName(asmLoc);
                string p1 = Path.Combine(dir, "Icons", "ESKD.ico");
                string p2 = Path.Combine(dir, "ESKD.ico");
                if (File.Exists(p1))
                {
                    this.Icon = new Icon(p1);
                    this.ShowIcon = true;
                }
                else if (File.Exists(p2))
                {
                    this.Icon = new Icon(p2);
                    this.ShowIcon = true;
                }
                else
                {
                    this.ShowIcon = false;
                }
            }
            catch
            {
                this.ShowIcon = false;
            }

            // ================= HEADER =================
            Panel pnlHeader = new Panel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.White,
                Padding = new Padding(24, 18, 24, 18)
            };
            pnlHeader.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(226, 232, 240), 1))
                {
                    e.Graphics.DrawLine(p, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1);
                }
            };

            TableLayoutPanel tblHeader = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent
            };
            tblHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tblHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // Badge "ЕСКД"
            Label lblBadge = new Label()
            {
                Text = "ЕСКД",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(29, 78, 216),
                BackColor = Color.FromArgb(239, 246, 255),
                AutoSize = true,
                Padding = new Padding(14, 10, 14, 10),
                Margin = new Padding(0, 2, 16, 2),
                TextAlign = ContentAlignment.MiddleCenter
            };
            lblBadge.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(191, 219, 254), 1.5f))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, lblBadge.Width - 1, lblBadge.Height - 1);
                }
            };

            // Header Texts (Title & Subtitle)
            FlowLayoutPanel pnlHeaderTexts = new FlowLayoutPanel()
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };

            Label lblHeaderTitle = new Label()
            {
                Text = "Настройки ЕСКД и штампа чертежа",
                Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };

            Label lblHeaderSub = new Label()
            {
                Text = "Синхронизация реквизитов ГОСТ 2.104, свойств материалов и центрирование массы",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(0)
            };

            pnlHeaderTexts.Controls.Add(lblHeaderTitle);
            pnlHeaderTexts.Controls.Add(lblHeaderSub);

            tblHeader.Controls.Add(lblBadge, 0, 0);
            tblHeader.Controls.Add(pnlHeaderTexts, 1, 0);
            pnlHeader.Controls.Add(tblHeader);

            // ================= FOOTER =================
            Panel pnlFooter = new Panel()
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.FromArgb(248, 250, 252),
                Padding = new Padding(24, 14, 24, 14)
            };
            pnlFooter.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(226, 232, 240), 1))
                {
                    e.Graphics.DrawLine(p, 0, 0, pnlFooter.Width, 0);
                }
            };

            TableLayoutPanel tblFooter = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent
            };
            tblFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tblFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            btnApplyNow = new Button()
            {
                Text = "Применить сейчас",
                AutoSize = true,
                MinimumSize = new Size(180, 38),
                Padding = new Padding(16, 6, 16, 6),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 41, 59),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Margin = new Padding(0)
            };
            btnApplyNow.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnApplyNow.Click += BtnApplyNow_Click;

            FlowLayoutPanel pnlRightButtons = new FlowLayoutPanel()
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };

            btnSave = new Button()
            {
                Text = "Сохранить",
                AutoSize = true,
                MinimumSize = new Size(140, 38),
                Padding = new Padding(20, 6, 20, 6),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.OK,
                Margin = new Padding(12, 0, 0, 0)
            };
            btnSave.FlatAppearance.BorderColor = Color.FromArgb(29, 78, 216);
            btnSave.Click += BtnSave_Click;

            btnCancel = new Button()
            {
                Text = "Отмена",
                AutoSize = true,
                MinimumSize = new Size(110, 38),
                Padding = new Padding(16, 6, 16, 6),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(71, 85, 105),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(0)
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            btnCancel.Click += (s, e) => this.Close();

            pnlRightButtons.Controls.Add(btnSave);
            pnlRightButtons.Controls.Add(btnCancel);

            tblFooter.Controls.Add(btnApplyNow, 0, 0);
            tblFooter.Controls.Add(pnlRightButtons, 1, 0);
            pnlFooter.Controls.Add(tblFooter);

            // ================= SCROLLABLE BODY =================
            Panel pnlBody = new Panel()
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(243, 244, 246),
                Padding = new Padding(24, 16, 24, 16)
            };

            // Container for all Cards
            TableLayoutPanel tblCards = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Transparent
            };
            tblCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // -------------------------------------------------------------
            // CARD 0: РЕЖИМ РАБОТЫ ФОНОВОЙ СЛУЖБЫ ЕСКД
            // -------------------------------------------------------------
            Panel cardService = CreateCardPanel();
            TableLayoutPanel tblService = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Transparent
            };
            tblService.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lblServiceTitle = new Label()
            {
                Text = "РЕЖИМ РАБОТЫ ФОНОВОЙ СЛУЖБЫ ЕСКД",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };

            chkServiceEnabled = new CheckBox()
            {
                Text = "Включить фоновую службу автоматического оформления ЕСКД",
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Checked = true,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 4, 0, 4)
            };

            lblServiceStatus = new Label()
            {
                Text = "🟢 Фоновая служба ЕСКД: ВКЛЮЧЕНА (автоматические триггеры при сохранении активны)",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(22, 101, 52),
                BackColor = Color.FromArgb(240, 253, 244),
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(0, 4, 0, 6)
            };

            Label lblServiceNote = new Label()
            {
                Text = "Когда флажок установлен: служба автоматически заполняет свойства ЕСКД (материалы, массу, обозначение и наименование) и центрирует реквизиты в штампе при сохранении, открытии и перестроении моделей.\nЕсли флажок снять: фоновые триггеры полностью отключаются (ручная синхронизация по кнопке «Синхронизировать ЕСКД» или «Применить сейчас» доступна в любой момент).",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 4)
            };

            chkServiceEnabled.CheckedChanged += (s, e) =>
            {
                if (chkServiceEnabled.Checked)
                {
                    lblServiceStatus.Text = "🟢 Фоновая служба ЕСКД: ВКЛЮЧЕНА (автоматические триггеры при сохранении активны)";
                    lblServiceStatus.ForeColor = Color.FromArgb(22, 101, 52);
                    lblServiceStatus.BackColor = Color.FromArgb(240, 253, 244);
                }
                else
                {
                    lblServiceStatus.Text = "🔴 Фоновая служба ЕСКД: ОТКЛЮЧЕНА (автоматические триггеры неактивны, доступен ручной запуск)";
                    lblServiceStatus.ForeColor = Color.FromArgb(153, 27, 27);
                    lblServiceStatus.BackColor = Color.FromArgb(254, 242, 242);
                }
            };

            tblService.Controls.Add(lblServiceTitle, 0, 0);
            tblService.Controls.Add(chkServiceEnabled, 0, 1);
            tblService.Controls.Add(lblServiceStatus, 0, 2);
            tblService.Controls.Add(lblServiceNote, 0, 3);
            cardService.Controls.Add(tblService);

            // -------------------------------------------------------------
            // CARD 1: РЕКВИЗИТЫ ОСНОВНОЙ НАДПИСИ (ШТАМПА)
            // -------------------------------------------------------------
            Panel cardProps = CreateCardPanel();
            TableLayoutPanel tblProps = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Color.Transparent
            };
            tblProps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lblPropsTitle = new Label()
            {
                Text = "1. РЕКВИЗИТЫ ОСНОВНОЙ НАДПИСИ (ШТАМПА)",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };

            Label lblPropsSub = new Label()
            {
                Text = "Синхронизировано с базой фамилий и организаций SWPlus (MProp / DProp)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 14)
            };

            TableLayoutPanel tblPropsGrid = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 3,
                BackColor = Color.Transparent
            };
            tblPropsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tblPropsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lblAuthor = new Label()
            {
                Text = "Разработал (Конструктор):",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(30, 41, 59),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 16, 6)
            };
            cmbAuthor = new ComboBox()
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Margin = new Padding(0, 4, 0, 8)
            };

            Label lblChecker = new Label()
            {
                Text = "Проверил:",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(30, 41, 59),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 16, 6)
            };
            cmbChecker = new ComboBox()
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Margin = new Padding(0, 4, 0, 8)
            };

            Label lblOrg = new Label()
            {
                Text = "Организация (Контора):",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = Color.FromArgb(30, 41, 59),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 16, 6)
            };
            cmbOrg = new ComboBox()
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Margin = new Padding(0, 4, 0, 4)
            };

            tblPropsGrid.Controls.Add(lblAuthor, 0, 0);
            tblPropsGrid.Controls.Add(cmbAuthor, 1, 0);
            tblPropsGrid.Controls.Add(lblChecker, 0, 1);
            tblPropsGrid.Controls.Add(cmbChecker, 1, 1);
            tblPropsGrid.Controls.Add(lblOrg, 0, 2);
            tblPropsGrid.Controls.Add(cmbOrg, 1, 2);

            chkAutoSplitName = new CheckBox()
            {
                Text = "Автоматически заполнять Обозначение и Наименование из имени файла",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Checked = true, // ALWAYS CHECKED BY DEFAULT
                Margin = new Padding(0, 12, 0, 2)
            };

            Label lblAutoSplitNameNote = new Label()
            {
                Text = "При сохранении имя файла делится по первому пробелу: Обозначение (до пробела) и Наименование (после пробела)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(24, 0, 0, 4)
            };

            tblProps.Controls.Add(lblPropsTitle, 0, 0);
            tblProps.Controls.Add(lblPropsSub, 0, 1);
            tblProps.Controls.Add(tblPropsGrid, 0, 2);
            tblProps.Controls.Add(chkAutoSplitName, 0, 3);
            tblProps.Controls.Add(lblAutoSplitNameNote, 0, 4);
            cardProps.Controls.Add(tblProps);

            // -------------------------------------------------------------
            // CARD 2: ПАРАМЕТРЫ РАСЧЕТА МАССЫ И ЧЕРТЕЖА
            // -------------------------------------------------------------
            Panel cardMass = CreateCardPanel();
            TableLayoutPanel tblMass = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 8,
                BackColor = Color.Transparent
            };
            tblMass.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lblMassTitle = new Label()
            {
                Text = "2. ПАРАМЕТРЫ РАСЧЕТА МАССЫ И ЧЕРТЕЖА",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };

            Label lblMassSub = new Label()
            {
                Text = "Автоматический пересчет массы и центрирование в ячейке штампа ГОСТ 2.104",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 14)
            };

            chkAutoMass = new CheckBox()
            {
                Text = "Автоматически вычислять и записывать массу в свойства модели",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Checked = true, // ALWAYS CHECKED BY DEFAULT
                Margin = new Padding(0, 0, 0, 2)
            };

            Label lblAutoMassNote = new Label()
            {
                Text = "Обновляет свойства «Масса_ФБ» и «Масса» при каждом сохранении и перестроении",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(24, 0, 0, 12)
            };

            FlowLayoutPanel pnlDecimals = new FlowLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(24, 0, 0, 14)
            };

            Label lblDecimals = new Label()
            {
                Text = "Точность расчета массы:",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(30, 41, 59),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, 8, 3)
            };

            numMassDecimals = new NumericUpDown()
            {
                Font = new Font("Segoe UI", 9F),
                Minimum = 0,
                Maximum = 4,
                Value = 2, // 2 DECIMALS BY DEFAULT
                Width = 55,
                Margin = new Padding(0, 0, 8, 0)
            };

            Label lblDecimalsNote = new Label()
            {
                Text = "знаков после запятой (по стандарту ЕСКД: 2)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, 0, 3)
            };

            pnlDecimals.Controls.Add(lblDecimals);
            pnlDecimals.Controls.Add(numMassDecimals);
            pnlDecimals.Controls.Add(lblDecimalsNote);

            chkAutoCenterMass = new CheckBox()
            {
                Text = "Автоматически выравнивать и центрировать наименование, массу, масштаб и материал в штампе чертежа",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Checked = true, // ALWAYS CHECKED BY DEFAULT
                Margin = new Padding(0, 0, 0, 2)
            };

            Label lblAutoCenterNote = new Label()
            {
                Text = "Выравнивание по ГОСТ 2.104: наименование (гр. 1), масса и масштаб на одной линии (гр. 5, 6), материал (гр. 3)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(24, 0, 0, 4)
            };

            Label lblFormatkaNote = new Label()
            {
                Text = "Применяется ко всем шаблонам форматок (A0-A4) и динамически при сохранении",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize = true,
                Margin = new Padding(24, 0, 0, 4)
            };

            tblMass.Controls.Add(lblMassTitle, 0, 0);
            tblMass.Controls.Add(lblMassSub, 0, 1);
            tblMass.Controls.Add(chkAutoMass, 0, 2);
            tblMass.Controls.Add(lblAutoMassNote, 0, 3);
            tblMass.Controls.Add(pnlDecimals, 0, 4);
            tblMass.Controls.Add(chkAutoCenterMass, 0, 5);
            tblMass.Controls.Add(lblAutoCenterNote, 0, 6);
            tblMass.Controls.Add(lblFormatkaNote, 0, 7);
            cardMass.Controls.Add(tblMass);

            // -------------------------------------------------------------
            // CARD 3: АВТОМАТИЧЕСКАЯ СИНХРОНИЗАЦИЯ МАТЕРИАЛОВ (ПО ТРИГГЕРУ)
            // -------------------------------------------------------------
            Panel cardMat = CreateCardPanel();
            TableLayoutPanel tblMat = new TableLayoutPanel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Color.Transparent
            };
            tblMat.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lblMatTitle = new Label()
            {
                Text = "3. СИНХРОНИЗАЦИЯ МАТЕРИАЛОВ И СПЕЦИФИКАЦИЙ (ПО ТРИГГЕРУ)",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };

            Label lblMatSub = new Label()
            {
                Text = "Срабатывает автоматически при назначении материала и при сохранении модели",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblMatRule1 = new Label()
            {
                Text = "✔ Для штампа чертежа: свойство «Материал_ФБ» (дробь ГОСТ или одна строка с авто-центрированием в ячейке)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(51, 65, 85),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };

            Label lblMatRule2 = new Label()
            {
                Text = "✔ Для спецификаций и ЛЗК: свойство «Материал» в линейном виде (Лист... / Ст3сп...)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(51, 65, 85),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };

            Label lblMatRule3 = new Label()
            {
                Text = "✔ Сортамент, ГОСТ_Материал, ГОСТ_Сортамент и масса полностью совместимы с SWPlus (MProp, SProp)",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(51, 65, 85),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };

            chkAutoSyncMaterials = new CheckBox()
            {
                Text = "Автоматически синхронизировать материалы при сохранении детали",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 6, 0, 8)
            };

            tblMat.RowCount = 6;
            tblMat.Controls.Add(lblMatTitle, 0, 0);
            tblMat.Controls.Add(lblMatSub, 0, 1);
            tblMat.Controls.Add(chkAutoSyncMaterials, 0, 2);
            tblMat.Controls.Add(lblMatRule1, 0, 3);
            tblMat.Controls.Add(lblMatRule2, 0, 4);
            tblMat.Controls.Add(lblMatRule3, 0, 5);
            cardMat.Controls.Add(tblMat);

            // Add all cards to table layout
            tblCards.Controls.Add(cardService, 0, 0);
            tblCards.Controls.Add(cardProps, 0, 1);
            tblCards.Controls.Add(cardMass, 0, 2);
            tblCards.Controls.Add(cardMat, 0, 3);

            pnlBody.Controls.Add(tblCards);

            // Add all main docked controls to Form
            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlHeader);
            this.Controls.Add(pnlFooter);

            this.AcceptButton = btnSave;
            this.CancelButton = btnCancel;
        }

        private Panel CreateCardPanel()
        {
            Panel card = new Panel()
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.White,
                Padding = new Padding(20, 16, 20, 16),
                Margin = new Padding(0, 0, 0, 14)
            };
            card.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(226, 232, 240), 1))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
                }
            };
            return card;
        }

        private void LoadSettings()
        {
            try
            {
                // 1. Load surnames from SWPlus text files
                HashSet<string> authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string famFile in GetSwPlusFamPaths())
                {
                    if (File.Exists(famFile))
                    {
                        try
                        {
                            string[] lines = File.ReadAllLines(famFile, Encoding.GetEncoding(1251));
                            foreach (string line in lines)
                            {
                                string t = line.Trim();
                                if (!string.IsNullOrEmpty(t)) authors.Add(t);
                            }
                        }
                        catch { }
                    }
                }

                // 2. Load firms from SWPlus text files (alternating lines: odd = firm name, even = classifier)
                HashSet<string> firms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string firmFile in GetSwPlusFirmPaths())
                {
                    if (File.Exists(firmFile))
                    {
                        try
                        {
                            string[] lines = File.ReadAllLines(firmFile, Encoding.GetEncoding(1251));
                            for (int i = 0; i < lines.Length; i += 2)
                            {
                                string t = lines[i].Trim();
                                if (!string.IsNullOrEmpty(t)) firms.Add(t);
                            }
                        }
                        catch { }
                    }
                }

                // 3. Load from registry
                // D-13: канонические дефолты этой станции (HKCU ESKD_Settings)
                string currentAuthor = "Лунин В.И.";
                string currentChecker = "";
                string currentOrg = "123";
                int serviceEnabled = 1;
                int autoSyncMat = 1;
                int autoMass = 1;
                int decimals = 2;
                int autoCenter = 1;
                int autoSplit = 1;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (key != null)
                    {
                        currentAuthor = (key.GetValue("Author") as string) ?? currentAuthor;
                        currentChecker = (key.GetValue("Checker") as string) ?? currentChecker;
                        currentOrg = (key.GetValue("Organization") as string) ?? currentOrg;
                        serviceEnabled = MaterialSyncEngine.ReadIntSafe(key, "ServiceEnabled", 1);
                        autoSyncMat = MaterialSyncEngine.ReadIntSafe(key, "AutoSyncMaterials", 1);
                        autoMass = MaterialSyncEngine.ReadIntSafe(key, "AutoMass", 1);
                        decimals = MaterialSyncEngine.ReadIntSafe(key, "MassDecimals", 2);
                        autoCenter = MaterialSyncEngine.ReadIntSafe(key, "AutoCenterMass", 1);
                        autoSplit = MaterialSyncEngine.ReadIntSafe(key, "AutoSplitName", 1);

                        string authorList = key.GetValue("AuthorList") as string;
                        if (!string.IsNullOrEmpty(authorList))
                        {
                            foreach (string a in authorList.Split(';'))
                            {
                                string t = a.Trim();
                                if (!string.IsNullOrEmpty(t)) authors.Add(t);
                            }
                        }

                        string checkerList = key.GetValue("CheckerList") as string;
                        if (!string.IsNullOrEmpty(checkerList))
                        {
                            foreach (string c in checkerList.Split(';'))
                            {
                                string t = c.Trim();
                                if (!string.IsNullOrEmpty(t)) authors.Add(t);
                            }
                        }
                    }
                }

                // Populate controls
                foreach (string a in authors)
                {
                    cmbAuthor.Items.Add(a);
                    cmbChecker.Items.Add(a);
                }
                cmbAuthor.Text = currentAuthor;
                cmbChecker.Text = currentChecker;

                foreach (string f in firms)
                {
                    cmbOrg.Items.Add(f);
                }
                if (!firms.Contains(currentOrg) && !string.IsNullOrEmpty(currentOrg))
                {
                    cmbOrg.Items.Add(currentOrg);
                }
                cmbOrg.Text = currentOrg;

                // Set Options (DEFAULTS ARE ALWAYS CHECKED)
                chkServiceEnabled.Checked = (serviceEnabled == 1);
                chkAutoSyncMaterials.Checked = (autoSyncMat == 1);
                chkAutoMass.Checked = (autoMass == 1);
                numMassDecimals.Value = Math.Max(0, Math.Min(4, decimals));
                chkAutoCenterMass.Checked = (autoCenter == 1);
                chkAutoSplitName.Checked = (autoSplit == 1);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка чтения настроек: " + ex.Message, "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveSettings()
        {
            try
            {
                string author = cmbAuthor.Text.Trim();
                string checker = cmbChecker.Text.Trim();
                string org = cmbOrg.Text.Trim();

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    if (key != null)
                    {
                        key.SetValue("ServiceEnabled", chkServiceEnabled.Checked ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("AutoSyncMaterials", chkAutoSyncMaterials.Checked ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("Author", author, RegistryValueKind.String);
                        key.SetValue("Checker", checker, RegistryValueKind.String);
                        key.SetValue("Organization", org, RegistryValueKind.String);
                        key.SetValue("AutoMass", chkAutoMass.Checked ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("MassDecimals", (int)numMassDecimals.Value, RegistryValueKind.DWord);
                        key.SetValue("AutoCenterMass", chkAutoCenterMass.Checked ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("AutoSplitName", chkAutoSplitName.Checked ? 1 : 0, RegistryValueKind.DWord);

                        UpdateRegistryList(key, "AuthorList", author);
                        if (!string.IsNullOrEmpty(checker))
                            UpdateRegistryList(key, "CheckerList", checker);
                        if (!string.IsNullOrEmpty(org))
                            UpdateRegistryList(key, "OrgList", org);
                    }
                }

                // Also sync back to SWPlus text files so MProp/DProp see the exact same values
                SyncFullListToSwPlus(GetSwPlusFamPaths(), author, checker, cmbAuthor.Items);
                SyncFirmsToSwPlus(GetSwPlusFirmPaths(), org, cmbOrg.Items);
                SyncToMPropIni(author);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка сохранения настроек: " + ex.Message, "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateRegistryList(RegistryKey key, string valName, string newItem)
        {
            if (string.IsNullOrEmpty(newItem)) return;
            string existing = key.GetValue(valName, "") as string;
            HashSet<string> items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            items.Add(newItem);
            if (!string.IsNullOrEmpty(existing))
            {
                foreach (string s in existing.Split(';'))
                {
                    string t = s.Trim();
                    if (!string.IsNullOrEmpty(t)) items.Add(t);
                }
            }
            key.SetValue(valName, string.Join(";", items), RegistryValueKind.String);
        }

        private void SyncFullListToSwPlus(string[] paths, string primary, string secondary, ComboBox.ObjectCollection existingItems)
        {
            List<string> orderedList = new List<string>();
            if (!string.IsNullOrEmpty(primary)) orderedList.Add(primary);
            if (!string.IsNullOrEmpty(secondary) && !orderedList.Contains(secondary)) orderedList.Add(secondary);

            if (existingItems != null)
            {
                foreach (object item in existingItems)
                {
                    string s = item != null ? item.ToString().Trim() : "";
                    if (!string.IsNullOrEmpty(s) && !orderedList.Contains(s))
                    {
                        orderedList.Add(s);
                    }
                }
            }

            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string[] fileLines = File.ReadAllLines(path, Encoding.GetEncoding(1251));
                        foreach (string fl in fileLines)
                        {
                            string t = fl.Trim();
                            if (!string.IsNullOrEmpty(t) && !orderedList.Contains(t))
                            {
                                orderedList.Add(t);
                            }
                        }
                    }
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        File.WriteAllLines(path, orderedList.ToArray(), Encoding.GetEncoding(1251));
                    }
                }
                catch { }
            }
        }

        private void SyncFirmsToSwPlus(string[] paths, string primaryOrg, ComboBox.ObjectCollection existingItems)
        {
            List<string> orderedFirms = new List<string>();
            if (!string.IsNullOrEmpty(primaryOrg)) orderedFirms.Add(primaryOrg);

            if (existingItems != null)
            {
                foreach (object item in existingItems)
                {
                    string s = item != null ? item.ToString().Trim() : "";
                    if (!string.IsNullOrEmpty(s) && !orderedFirms.Contains(s))
                    {
                        orderedFirms.Add(s);
                    }
                }
            }

            foreach (string path in paths)
            {
                try
                {
                    Dictionary<string, string> existingClassifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(path))
                    {
                        string[] fileLines = File.ReadAllLines(path, Encoding.GetEncoding(1251));
                        for (int i = 0; i < fileLines.Length; i += 2)
                        {
                            string f = fileLines[i].Trim();
                            string c = (i + 1 < fileLines.Length) ? fileLines[i + 1].Trim() : "";
                            if (!string.IsNullOrEmpty(f))
                            {
                                if (!existingClassifiers.ContainsKey(f)) existingClassifiers[f] = c;
                                if (!orderedFirms.Contains(f)) orderedFirms.Add(f);
                            }
                        }
                    }

                    List<string> outputLines = new List<string>();
                    foreach (string f in orderedFirms)
                    {
                        outputLines.Add(f);
                        string c = "";
                        if (existingClassifiers.ContainsKey(f)) c = existingClassifiers[f];
                        outputLines.Add(c);
                    }

                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        File.WriteAllLines(path, outputLines.ToArray(), Encoding.GetEncoding(1251));
                    }
                }
                catch { }
            }
        }

        private void SyncToMPropIni(string author)
        {
            try
            {
                string[] iniPaths = FindCandidatePaths(@"Макросы_SW_ZTool\SWPlusMacro_v_2018_SP0.0\MProp\MProp.ini");
                foreach (string iniPath in iniPaths)
                {
                    if (!File.Exists(iniPath)) continue;

                    // D-7: индекс выбранной в GUI фамилии — позиция в MProp_Fam.txt
                    // (раньше всегда писался 0, и выбранная фамилия не попадала в MProp).
                    int authorIndex = 0;
                    if (!string.IsNullOrEmpty(author))
                    {
                        string famPath = Path.Combine(Path.GetDirectoryName(iniPath), "MProp_Fam.txt");
                        if (File.Exists(famPath))
                        {
                            string[] fams = File.ReadAllLines(famPath, Encoding.GetEncoding(1251));
                            for (int i = 0; i < fams.Length; i++)
                            {
                                if (string.Equals(fams[i].Trim(), author.Trim(), StringComparison.OrdinalIgnoreCase))
                                {
                                    authorIndex = i;
                                    break;
                                }
                            }
                        }
                    }

                    List<string> lines = new List<string>(File.ReadAllLines(iniPath, Encoding.GetEncoding(1251)));
                    while (lines.Count < 5) lines.Add("0");
                    lines[0] = authorIndex.ToString();
                    // Note: Line 6 (index 5) is MIni3 which is the Material Database name (.sldmat), NOT organization!
                    // We preserve line 6 intact so MProp never warns about missing material database.
                    File.WriteAllLines(iniPath, lines.ToArray(), Encoding.GetEncoding(1251));
                }
            }
            catch { }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            SaveSettings();
            if (_swApp != null && chkServiceEnabled.Checked)
            {
                try
                {
                    ModelDoc2 doc = (ModelDoc2)_swApp.ActiveDoc;
                    if (doc != null)
                    {
                        MaterialSyncEngine.SyncModelProperties(doc, _swApp, true, true);
                        // Zero-Drift: ForceRebuild3 смещает заметки чертежа (до 9 мм)
                        if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                        {
                            doc.ForceRebuild3(true);
                        }
                    }
                }
                catch { }
            }
            string statusMsg = chkServiceEnabled.Checked
                ? "Настройки ЕСКД успешно сохранены и синхронизированы с макросами SWPlus!\n\nФоновая служба ЕСКД: ВКЛЮЧЕНА (автоматическое оформление активно)."
                : "Настройки ЕСКД успешно сохранены и синхронизированы с макросами SWPlus!\n\nФоновая служба ЕСКД: ОТКЛЮЧЕНА (автоматические фоновые триггеры неактивны, доступен ручной запуск по кнопке «Синхронизировать ЕСКД»).";
            MessageBox.Show(statusMsg, "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Information);
            this.Close();
        }

        private void BtnApplyNow_Click(object sender, EventArgs e)
        {
            SaveSettings();
            if (_swApp != null)
            {
                try
                {
                    ModelDoc2 doc = (ModelDoc2)_swApp.ActiveDoc;
                    if (doc != null)
                    {
                        MaterialSyncEngine.SyncModelProperties(doc, _swApp, true, true);
                        // Zero-Drift: ForceRebuild3 смещает заметки чертежа (до 9 мм)
                        if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                        {
                            doc.ForceRebuild3(true);
                        }
                        string docTypeTitle = doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING ? "чертежа (штамп и ссылочная модель)" : "модели";
                        MessageBox.Show(string.Format("Настройки ЕСКД применены к активному документу!\n\nРеквизиты, масса и свойства {0} успешно обновлены.", docTypeTitle),
                            "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        this.Close();
                        return;
                    }
                    else
                    {
                        MessageBox.Show("Настройки сохранены в реестр и файлы SWPlus!\n\n(В данный момент нет открытых документов в SolidWorks. Настройки применятся автоматически при открытии моделей или чертежей).",
                            "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        this.Close();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Ошибка синхронизации документа: " + ex.Message, "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            MessageBox.Show("Настройки сохранены.", "Настройки ЕСКД", MessageBoxButtons.OK, MessageBoxIcon.Information);
            this.Close();
        }
    }
}
