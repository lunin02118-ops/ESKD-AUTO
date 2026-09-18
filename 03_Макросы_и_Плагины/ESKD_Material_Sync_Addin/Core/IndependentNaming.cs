using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Одна деталь, ставшая независимой: откуда взята, куда положена и что стало с её чертежом.</summary>
    public sealed class IndependentEntry
    {
        /// <summary>Файл эталона или чужого изделия, с которого сделана копия.</summary>
        public string Source = "";
        /// <summary>Новый файл в «01_3D» изделия.</summary>
        public string Target = "";
        /// <summary>Копия чертежа, если её просили и она получилась.</summary>
        public string Drawing = "";
        /// <summary>Сколько экземпляров в сборке перепривязано на новую модель (Т-21).</summary>
        public int Instances = 1;
        /// <summary>Сколько видов чертежа переведено на новую модель (Т-22).</summary>
        public int Views;
        /// <summary>Оборванные размеры копии чертежа — их правит конструктор (Т-22, Т-24).</summary>
        public int Dangling;
    }

    /// <summary>
    /// Отчёт кнопки К-1 «Сделать независимым с чертежом» (ТЗ-02 Т-24): что создано, сколько экземпляров
    /// перепривязано, какие размеры оборвались и не изменился ли эталон. Текст отделён от SolidWorks,
    /// чтобы его проверяли юнит-тесты и читал человек.
    /// </summary>
    public sealed class IndependentLog
    {
        public string Product = "";
        public string User = "";
        public DateTime Time = DateTime.Now;
        public readonly List<IndependentEntry> Created = new List<IndependentEntry>();
        public readonly List<string> Skipped = new List<string>();
        /// <summary>Эталон → «совпала» или «РАЗОШЛАСЬ»: контрольная сумма исходного файла до и после (Т-24).</summary>
        public readonly List<KeyValuePair<string, bool>> Sources = new List<KeyValuePair<string, bool>>();

        public void Skip(string document, string reason)
        {
            Skipped.Add((document ?? "") + " — " + (reason ?? ""));
        }

        /// <summary>Исходный файл не изменён: сумма до и после совпала.</summary>
        public void Source(string path, bool unchanged)
        {
            Sources.Add(new KeyValuePair<string, bool>(Path.GetFileName(path ?? ""), unchanged));
        }

        public int Dangling
        {
            get
            {
                int sum = 0;
                foreach (IndependentEntry e in Created) sum += e.Dangling;
                return sum;
            }
        }

        public bool SourcesUntouched
        {
            get
            {
                foreach (KeyValuePair<string, bool> pair in Sources) if (!pair.Value) return false;
                return true;
            }
        }

        public string Text()
        {
            StringBuilder sb = new StringBuilder();
            int instances = 0;
            foreach (IndependentEntry e in Created) instances += e.Instances;
            sb.AppendLine("Сделано независимым");
            sb.AppendLine("Изделие:  " + Product);
            sb.AppendLine("Сделал:   " + User + ", " + Time.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")));
            sb.AppendLine("Создано:  " + Created.Count + ", перепривязано экземпляров: " + instances +
                ", пропущено: " + Skipped.Count);
            sb.AppendLine();
            if (Created.Count == 0) sb.AppendLine("Ничего не создано.");
            else
            {
                sb.AppendLine("Создано:");
                foreach (IndependentEntry e in Created)
                {
                    sb.AppendLine("  " + Path.GetFileName(e.Target) + "  ← " + Path.GetFileName(e.Source) +
                        " (экземпляров: " + e.Instances + ")");
                    if (e.Drawing.Length > 0)
                        sb.AppendLine("    чертёж: " + Path.GetFileName(e.Drawing) + ", видов перепривязано: " + e.Views +
                            (e.Dangling > 0 ? ", оборванных размеров: " + e.Dangling : ""));
                }
            }
            if (Skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Пропущено:");
                foreach (string line in Skipped) sb.AppendLine("  " + line);
            }
            if (Sources.Count > 0)
            {
                sb.AppendLine();
                // Смысл проверки (Т-24): эталон в «02_БАЗА» общий для всех заказов, кнопка его не трогает.
                sb.AppendLine(SourcesUntouched
                    ? "Исходные модели не изменены (контрольные суммы совпали):"
                    : "ВНИМАНИЕ: исходная модель изменена — проверьте эталон:");
                foreach (KeyValuePair<string, bool> pair in Sources)
                    sb.AppendLine("  " + (pair.Value ? "не изменён" : "ИЗМЕНЁН   ") + "  " + pair.Key);
            }
            if (Dangling > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Оборванных размеров в копиях чертежей: " + Dangling + " — поправьте их вручную.");
            }
            return sb.ToString();
        }
    }

    /// <summary>Имена и места файлов кнопки К-1 (ТЗ-02 Т-20…Т-24).</summary>
    public static class IndependentNaming
    {
        /// <summary>
        /// Новый файл модели: «&lt;Обозначение&gt; &lt;Наименование&gt;» в «01_3D» изделия с расширением исходного.
        /// Имя занято — к нему добавляется «_2», «_3»…: терять чужую работу молчаливой перезаписью нельзя.
        /// </summary>
        public static string TargetPath(string modelsFolder, string designation, string name, string sourcePath)
        {
            string stem = ExportNaming.Stem(designation, name, sourcePath);
            string extension = Path.GetExtension(sourcePath ?? "");
            string path = Path.Combine(modelsFolder ?? "", stem + extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(modelsFolder ?? "", stem + "_" + i + extension);
            return path;
        }

        /// <summary>Чертёж рядом с моделью: тот же путь с расширением «.slddrw».</summary>
        public static string DrawingOf(string modelPath)
        {
            return Path.ChangeExtension(modelPath ?? "", ".slddrw");
        }
    }
}
