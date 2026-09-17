using System;
using System.IO;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>Имена и отчёт кнопки К-1 «Сделать независимым с чертежом» (ТЗ-02 Т-20…Т-24).</summary>
    public static class IndependentTests
    {
        private const string Product = @"\\Synology_TR\Конструкторский отдел\_Заявки\2026-014 Школа\02_Металл\И01_ТС-52_Стол";

        public static void Test_New_model_goes_to_models_folder()
        {
            string models = Path.Combine(Product, "01_3D");
            Assert.AreEqual(Path.Combine(models, "ТС-52.00.01.004 Стойка.sldprt"),
                IndependentNaming.TargetPath(models, "ТС-52.00.01.004", "Стойка", @"Y:\02_БАЗА\Стойка.sldprt"),
                "новая деталь — в 01_3D изделия");
            Assert.AreEqual(Path.Combine(models, "Стойка.sldasm"),
                IndependentNaming.TargetPath(models, "", "", @"Y:\02_БАЗА\Стойка.sldasm"),
                "обозначения нет — имя от исходного файла, расширение сохраняется");
            Assert.AreEqual(Path.Combine(Product, IndependentNaming.ReportName), IndependentNaming.ReportPath(Product),
                "отчёт — в папке изделия");
            Assert.AreEqual(Path.Combine(models, "ТС-52.00.01.004 Стойка.slddrw"),
                IndependentNaming.DrawingOf(Path.Combine(models, "ТС-52.00.01.004 Стойка.sldprt")),
                "чертёж лежит рядом с моделью");
        }

        public static void Test_Existing_name_is_not_overwritten()
        {
            string models = Path.Combine(Path.GetTempPath(), "eskd_indep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(models);
            try
            {
                string first = IndependentNaming.TargetPath(models, "ТС-52.00.01.004", "Стойка", "Стойка.sldprt");
                File.WriteAllText(first, "занято");
                string second = IndependentNaming.TargetPath(models, "ТС-52.00.01.004", "Стойка", "Стойка.sldprt");
                Assert.AreEqual(Path.Combine(models, "ТС-52.00.01.004 Стойка_2.sldprt"), second,
                    "чужую работу молчаливой перезаписью не теряем");
            }
            finally
            {
                Directory.Delete(models, true);
            }
        }

        public static void Test_Report_tells_what_was_created()
        {
            IndependentLog log = new IndependentLog
            {
                Product = "ТС-52",
                User = "Лунин В.И.",
                Time = new DateTime(2026, 9, 18, 9, 30, 0)
            };
            log.Created.Add(new IndependentEntry
            {
                Source = @"Y:\02_БАЗА\Стойка.sldprt",
                Target = Path.Combine(Product, "01_3D", "ТС-52.00.01.004 Стойка.sldprt"),
                Drawing = Path.Combine(Product, "01_3D", "ТС-52.00.01.004 Стойка.slddrw"),
                Instances = 3,
                Views = 4,
                Dangling = 2
            });
            log.Skip("Винт ISO 7380 M8x16.sldprt", "покупное или стандартное изделие: оно приходит готовым");
            log.Source(@"Y:\02_БАЗА\Стойка.sldprt", true);

            string text = log.Text();
            Assert.IsTrue(text.Contains("Изделие:  ТС-52"), text);
            Assert.IsTrue(text.Contains("Сделал:   Лунин В.И., 18.09.2026 09:30"), text);
            Assert.IsTrue(text.Contains("Создано:  1, перепривязано экземпляров: 3, пропущено: 1"), text);
            Assert.IsTrue(text.Contains("ТС-52.00.01.004 Стойка.sldprt  ← Стойка.sldprt (экземпляров: 3)"), text);
            Assert.IsTrue(text.Contains("видов перепривязано: 4, оборванных размеров: 2"), text);
            Assert.IsTrue(text.Contains("покупное или стандартное изделие"), text);
            Assert.IsTrue(text.Contains("Исходные модели не изменены"), text);
            Assert.AreEqual(2, log.Dangling, "оборванные размеры считаются по всем чертежам");
            Assert.IsTrue(log.SourcesUntouched, "эталон не изменён");
        }

        public static void Test_Changed_etalon_is_shouted_about()
        {
            // Т-24: если контрольная сумма эталона разошлась, это главная новость отчёта, а не мелкая строка.
            IndependentLog log = new IndependentLog { Product = "ТС-52", User = "И" };
            log.Source(@"Y:\02_БАЗА\Стойка.sldprt", false);
            string text = log.Text();
            Assert.IsFalse(log.SourcesUntouched, "сумма разошлась");
            Assert.IsTrue(text.Contains("ВНИМАНИЕ: исходная модель изменена"), text);
            Assert.IsTrue(text.Contains("ИЗМЕНЁН"), text);
            Assert.IsTrue(text.Contains("Ничего не создано."), text);
        }
    }
}
