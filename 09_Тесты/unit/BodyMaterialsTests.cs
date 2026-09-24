using System;
using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Материал по телам (сверка SW API 23.09.2026, №31 и №33): свой материал тела перекрывает материал детали. Стоящий
    /// материал позиции решает, спрашивать ли конструктора; фактический материал детали — что писать в графу 3.
    /// </summary>
    public static class BodyMaterialsTests
    {
        private const string Tube = "Труба 40х20х1,5 ГОСТ 8645-68 / 08пс ГОСТ 13663-86";
        private const string Oval = "Труба плоскоовальная 40х20х1,5 / 08пс";
        private const string Sheet = "Лист 8,0 ГОСТ 19903-2015 / Ст3сп ГОСТ 14637-89";

        private static bool FitsTube(string name)
        {
            return name == Tube || name == Oval;
        }

        public static void Test_Position_current_by_body_materials()
        {
            Assert.AreEqual(Tube, StockCatalog.PositionCurrent(new[] { Tube, Tube }, FitsTube), "у всех тел один — он");
            Assert.AreEqual(Sheet, StockCatalog.PositionCurrent(new[] { Sheet, "" }, FitsTube),
                "неподходящий у части тел — он: заменить можно только с согласия");
            Assert.AreEqual("", StockCatalog.PositionCurrent(new[] { Tube, "" }, FitsTube), "подходящий и пустые — дозаполнить");
            Assert.AreEqual(Tube, StockCatalog.PositionCurrent(new[] { Tube, Oval }, FitsTube), "оба подходят — первый");
            Assert.AreEqual(Sheet, StockCatalog.PositionCurrent(new[] { Tube, Sheet }, FitsTube), "неподходящий важнее порядка");
            Assert.AreEqual("", StockCatalog.PositionCurrent(new[] { "", " " }, FitsTube), "материала нет ни у одного");
            Assert.AreEqual("", StockCatalog.PositionCurrent(null, FitsTube), "тел нет");
            Assert.AreEqual(Tube, StockCatalog.PositionCurrent(new[] { Tube, Oval }, null), "без правила подбора — первый");
        }

        public static void Test_Actual_material_by_bodies()
        {
            List<string> mixed = new List<string>();
            Assert.AreEqual(Tube, BodyMaterials.ActualMaterial(new[] { Tube, Tube }, "", mixed), "свой у всех тел");
            Assert.AreEqual(Tube, BodyMaterials.ActualMaterial(new[] { Tube }, Sheet, mixed), "свой перекрывает материал детали");
            Assert.AreEqual(Sheet, BodyMaterials.ActualMaterial(new[] { "", "" }, Sheet, mixed), "своего нет — материал детали");
            Assert.IsNull(BodyMaterials.ActualMaterial(new[] { "", "" }, null, mixed), "нет никакого — как у детали");
            Assert.AreEqual(Sheet, BodyMaterials.ActualMaterial(new string[0], Sheet, mixed), "тел нет — материал детали");
            Assert.AreEqual(0, mixed.Count, "материал один — разных нет");
            Assert.AreEqual(Tube, BodyMaterials.ActualMaterial(new[] { Tube, "" }, Tube, mixed), "тело без своего берёт тот же");
            Assert.AreEqual(0, mixed.Count, "свой совпал с материалом детали — разных нет");
        }

        public static void Test_Mixed_body_materials_are_listed()
        {
            List<string> mixed = new List<string>();
            Assert.AreEqual("", BodyMaterials.ActualMaterial(new[] { Tube, "" }, "", mixed), "у части тел никакого — как у детали");
            Assert.AreEqual(2, mixed.Count, string.Join("|", mixed.ToArray()));
            Assert.IsTrue(mixed.Contains(Tube) && mixed.Contains(""), "труба и тела без материала");
            mixed.Clear();
            Assert.AreEqual(Oval, BodyMaterials.ActualMaterial(new[] { Tube, Sheet }, Oval, mixed), "разные — как у детали (MAT-15)");
            Assert.AreEqual(Tube + "|" + Sheet, string.Join("|", mixed.ToArray()), "разные материалы — по порядку тел");
        }

        public static void Test_Body_materials_belong_to_active_configuration_only()
        {
            // Ревью 23.09.2026: GetBodies2 отдаёт тела активной конфигурации. Материал тел другого исполнения по ним не
            // узнать — у «01» свои тела, — и проверка писала «исполнение «01»: материал не назначен».
            Assert.IsTrue(BodyMaterials.BodiesBelongTo("00", "00"), "активное исполнение");
            Assert.IsFalse(BodyMaterials.BodiesBelongTo("01", "00"), "тела другого исполнения не видны");
            Assert.IsTrue(BodyMaterials.BodiesBelongTo("00<Как сварено>", "00"), "техническая производная активного");
            Assert.IsTrue(BodyMaterials.BodiesBelongTo("00", "00SM-FLAT-PATTERN"), "активна развёртка — тела её исполнения");
            Assert.IsFalse(BodyMaterials.BodiesBelongTo("01<Как сварено>", "00"), "производная другого исполнения");
            Assert.IsFalse(BodyMaterials.BodiesBelongTo("01", ""), "активная не прочитана — тела не при чём");
        }

        public static void Test_Actual_material_of_other_execution()
        {
            // Ревью 23.09.2026: тела видны только активной конфигурации. У исполнения с теми же телами материал тел
            // читается по его имени и верен; не нашёлся, а материалы тел в детали в ходу, — «не узнать» (known = false):
            // «Материал_Строка» не стирается, проверка не пишет «не назначен». Материалов тел нет вовсе — решает материал
            // детали, и его нет — «не назначен» (K08).
            bool known;
            List<string> mixed = new List<string>();
            Assert.AreEqual(Tube, BodyMaterials.Actual(new[] { Tube, Tube }, new[] { Tube, Tube }, "", false, mixed, out known),
                "те же тела, материал на телах");
            Assert.IsTrue(known, "узнали");
            Assert.AreEqual(0, mixed.Count, "у чужого исполнения разнобой тел не сообщается");
            Assert.IsNull(BodyMaterials.Actual(new[] { "", "" }, new[] { Tube, "" }, "", false, mixed, out known),
                "тела активного исполнения ничего не говорят про это");
            Assert.IsFalse(known, "материалы тел в ходу — не узнать без переключения");
            Assert.IsNull(BodyMaterials.Actual(new[] { "", "" }, new[] { "", "" }, "", false, mixed, out known), "материала нет");
            Assert.IsTrue(known, "материалов тел в детали нет — «не назначен» верно");
            Assert.AreEqual(Sheet, BodyMaterials.Actual(new[] { "" }, new[] { Tube }, Sheet, false, mixed, out known), "материал детали");
            Assert.IsTrue(known, "материал детали есть — узнали");
            Assert.AreEqual(Oval, BodyMaterials.Actual(new[] { Tube, Sheet }, new[] { Tube, Sheet }, Oval, true, mixed, out known),
                "своя конфигурация — как ActualMaterial");
            Assert.IsTrue(known && mixed.Count == 2, "своя конфигурация: разнобой сообщается");
        }
    }
}
