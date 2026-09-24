using System;
using ESKD.MaterialSync.Core;
using SolidWorks.Interop.swconst;

namespace ESKD.Tests
{
    /// <summary>
    /// Коды открытия и сохранения SolidWorks словами (сверка SW API 23.09.2026, №17 и №28): в отчётах было «чертёж не
    /// открылся» без причины и «ответьте «Да»» на детали, открытой только для чтения. Ядро на SolidWorks не ссылается —
    /// совпадение кодов с swconst проверяется здесь.
    /// </summary>
    public static class SwCodesTests
    {
        public static void Test_Codes_match_SolidWorks()
        {
            Assert.AreEqual((int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen, SwCodes.OpenSameTitle, "одноимённый уже открыт");
            Assert.AreEqual((int)swFileLoadError_e.swFileNotFoundError, SwCodes.OpenNotFound, "не найден");
            Assert.AreEqual((int)swFileLoadError_e.swFutureVersion, SwCodes.OpenFutureVersion, "новая версия");
            Assert.AreEqual((int)swFileLoadError_e.swLowResourcesError, SwCodes.OpenLowResources, "мало памяти");
            Assert.AreEqual((int)swFileLoadError_e.swApplicationBusy, SwCodes.OpenBusy, "SolidWorks занят");
            Assert.AreEqual((int)swFileSaveError_e.swReadOnlySaveError, SwCodes.SaveReadOnly, "только для чтения");
            Assert.AreEqual((int)swFileSaveError_e.swFileLockError, SwCodes.SaveLocked, "файл занят");
            Assert.AreEqual((int)swFileSaveWarning_e.swFileSaveWarning_RebuildError, SwCodes.SaveWarningRebuildError, "ошибки перестроения");
            Assert.AreEqual((int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_NoError, SwCodes.BodyMaterialNoError, "материал встал");
            Assert.AreEqual((int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_ReadOnly, SwCodes.BodyMaterialReadOnly, "тело: только чтение");
            Assert.AreEqual((int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_ExternalReference,
                SwCodes.BodyMaterialExternalReference, "тело из другой детали");
            Assert.AreEqual((int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_RolledBackState,
                SwCodes.BodyMaterialRolledBack, "дерево откатано");
            Assert.AreEqual((int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_InvalidConfigName,
                SwCodes.BodyMaterialInvalidConfiguration, "нет исполнения");
            Assert.AreEqual((int)swBodyMaterialApplicationError_e.swBodyMaterialApplicationError_InvalidMaterialNameOrDbName,
                SwCodes.BodyMaterialInvalidMaterial, "нет материала в библиотеке");
        }

        public static void Test_Open_problem_is_explained()
        {
            Assert.IsTrue(SwCodes.OpenProblem(SwCodes.OpenSameTitle | 1).Contains("одноимённый"), "одноимённый документ другого заказа");
            Assert.IsTrue(SwCodes.OpenProblem(SwCodes.OpenNotFound).Contains("не найден"), "нет файла");
            Assert.IsTrue(SwCodes.OpenProblem(SwCodes.OpenFutureVersion).Contains("более новой версии"), "новая версия");
            Assert.AreEqual("код SolidWorks 1", SwCodes.OpenProblem(1), "неизвестный код — числом");
            Assert.AreEqual("", SwCodes.OpenProblem(0), "кода нет — причины нет");
        }

        public static void Test_Body_material_problem_is_explained()
        {
            Assert.IsTrue(SwCodes.BodyMaterialProblem(SwCodes.BodyMaterialReadOnly).Contains("только для чтения"), "только чтение");
            Assert.IsTrue(SwCodes.BodyMaterialProblem(SwCodes.BodyMaterialExternalReference).Contains("другой детали"), "чужое тело");
            Assert.IsTrue(SwCodes.BodyMaterialProblem(SwCodes.BodyMaterialRolledBack).Contains("полосу отката"), "откат");
            Assert.IsTrue(SwCodes.BodyMaterialProblem(SwCodes.BodyMaterialInvalidConfiguration).Contains("исполнение"), "исполнение");
            Assert.IsTrue(SwCodes.BodyMaterialProblem(SwCodes.BodyMaterialInvalidMaterial).Contains("библиотек"), "библиотека");
            Assert.IsTrue(SwCodes.BodyMaterialProblem(-1).Contains("(код -1)"), "неизвестный отказ — числом");
            Assert.IsTrue(SwCodes.BodyMaterialProblem(99).Contains("(код 99)"), "новый код — числом");
        }

        public static void Test_Save_problem_is_explained()
        {
            Assert.IsTrue(SwCodes.SaveProblem(SwCodes.SaveReadOnly).Contains("только для чтения"), "только для чтения");
            Assert.IsTrue(SwCodes.SaveProblem(SwCodes.SaveLocked).Contains("занят"), "занят");
            Assert.AreEqual("код SolidWorks 64", SwCodes.SaveProblem(64), "неизвестный код — числом");
        }
    }
}
