namespace ESKD.MaterialSync.Core
{
    /// <summary>
    /// Коды открытия и сохранения SolidWorks (swFileLoadError_e, swFileSaveError_e, swFileSaveWarning_e) и их смысл словами
    /// для отчётов (сверка SW API 23.09.2026, №17 и №28): было «чертёж не открылся» без причины, а на детали, открытой только
    /// для чтения, — совет «ответьте «Да»», хотя сохранить её нельзя. Ядро на SolidWorks не ссылается: совпадение чисел с
    /// swconst проверяет юнит-тест.
    /// </summary>
    public static class SwCodes
    {
        public const int OpenNotFound = 2;             // swFileNotFoundError
        public const int OpenFutureVersion = 8192;     // swFutureVersion
        public const int OpenSameTitle = 65536;        // swFileWithSameTitleAlreadyOpen
        public const int OpenLowResources = 262144;    // swLowResourcesError
        public const int OpenBusy = 8388608;           // swApplicationBusy

        public const int SaveReadOnly = 2;             // swReadOnlySaveError
        public const int SaveLocked = 16;              // swFileLockError
        public const int SaveWarningRebuildError = 1;  // swFileSaveWarning_RebuildError

        public const int BodyMaterialNoError = 1;               // swBodyMaterialApplicationError_NoError
        public const int BodyMaterialReadOnly = 2;              // swBodyMaterialApplicationError_ReadOnly
        public const int BodyMaterialExternalReference = 3;     // swBodyMaterialApplicationError_ExternalReference
        public const int BodyMaterialRolledBack = 4;            // swBodyMaterialApplicationError_RolledBackState
        public const int BodyMaterialInvalidConfiguration = 5;  // swBodyMaterialApplicationError_InvalidConfigName
        public const int BodyMaterialInvalidMaterial = 6;       // swBodyMaterialApplicationError_InvalidMaterialNameOrDbName

        /// <summary>Почему документ не открылся; кода нет — пусто.</summary>
        public static string OpenProblem(int errors)
        {
            if ((errors & OpenSameTitle) != 0)
                return "в SolidWorks уже открыт одноимённый документ из другой папки (другого заказа?) — закройте его и повторите";
            if ((errors & OpenNotFound) != 0) return "файл не найден";
            if ((errors & OpenFutureVersion) != 0) return "файл сохранён в более новой версии SolidWorks";
            if ((errors & OpenLowResources) != 0) return "SolidWorks не хватило памяти";
            if ((errors & OpenBusy) != 0) return "SolidWorks занят — повторите";
            return errors != 0 ? "код SolidWorks " + errors : "";
        }

        /// <summary>
        /// Почему SolidWorks не поставил материал телу (Body2.SetMaterialProperty; сверка SW API 23.09.2026, №38): в замечании
        /// стояло «(код 4)» без объяснения.
        /// </summary>
        public static string BodyMaterialProblem(int code)
        {
            switch (code)
            {
                case BodyMaterialReadOnly: return "деталь открыта только для чтения";
                case BodyMaterialExternalReference:
                    return "тело пришло из другой детали (вставленной или производной) — материал ставится в ней";
                case BodyMaterialRolledBack: return "дерево откатано — верните полосу отката в конец дерева";
                case BodyMaterialInvalidConfiguration: return "исполнение не найдено";
                case BodyMaterialInvalidMaterial: return "материала нет в подключённой библиотеке материалов";
                default: return "SolidWorks отказал (код " + code + ")";
            }
        }

        /// <summary>Почему документ не сохранился.</summary>
        public static string SaveProblem(int errors)
        {
            if ((errors & SaveReadOnly) != 0) return "открыт только для чтения";
            if ((errors & SaveLocked) != 0) return "файл занят другим пользователем";
            return "код SolidWorks " + errors;
        }
    }
}
