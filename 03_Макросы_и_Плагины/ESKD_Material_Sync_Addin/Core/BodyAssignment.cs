using System;
using System.Collections.Generic;

namespace ESKD.MaterialSync.Core
{
    /// <summary>Итог назначения материала телам позиции.</summary>
    public sealed class BodyAssignmentOutcome
    {
        public bool Ok;
        /// <summary>Почему не назначено — словами конструктора; пусто, если назначено.</summary>
        public string Failure = "";
        /// <summary>Тела, у которых материал сменился, а вернуть прежний не удалось.</summary>
        public readonly List<string> Stuck = new List<string>();
    }

    /// <summary>
    /// Материал телам позиции списка вырезов — всем или ни одному (сверка SW API 23.09.2026, №38). Раньше при отказе
    /// SolidWorks на очередном теле пройденные тела оставались с новым материалом, и в одной позиции выходили разные
    /// материалы. Отделено от SolidWorks ради юнит-тестов: тела, чтение, назначение и возврат — через делегаты.
    /// </summary>
    public static class BodyAssignment
    {
        /// <param name="name">Имя тела для отчёта.</param>
        /// <param name="own">Свой материал тела сейчас; «» — своего нет.</param>
        /// <param name="set">Назначить материал; ответ — код swBodyMaterialApplicationError_e.</param>
        /// <param name="notApplied">Из тел, где SolidWorks ответил «успех», — те, у которых материал не встал.</param>
        /// <param name="restore">Вернуть телу прежний свой материал («» — снять свой); false — не удалось.</param>
        /// <param name="problem">Код отказа словами.</param>
        public static BodyAssignmentOutcome Apply<T>(IList<T> bodies, Func<T, string> name, Func<T, string> own, Func<T, int> set,
            Func<List<T>, List<T>> notApplied, Func<T, string, bool> restore, Func<int, string> problem)
        {
            BodyAssignmentOutcome outcome = new BodyAssignmentOutcome();
            List<T> done = new List<T>();
            List<string> before = new List<string>();
            foreach (T body in bodies)
            {
                string old = own(body) ?? "";
                int code = set(body);
                if (code != SwCodes.BodyMaterialNoError)
                {
                    outcome.Failure = "телу «" + name(body) + "»: " + problem(code);
                    break;
                }
                done.Add(body);
                before.Add(old);
            }
            // «Успех» ещё не материал: на SolidWorks 2025 назначение телу отвечает NoError и ничего не меняет (e2e T14).
            List<T> missed = done.Count > 0 ? notApplied(done) ?? new List<T>() : new List<T>();
            if (outcome.Failure.Length == 0 && missed.Count > 0)
            {
                List<string> names = missed.ConvertAll(b => name(b));
                outcome.Failure = "SolidWorks не поставил его телам «" + string.Join("», «", names.ToArray()) + "» — назначьте его " +
                    "этим телам вручную (список вырезов или «Твёрдые тела» → правой кнопкой → «Материал»)";
            }
            if (outcome.Failure.Length == 0)
            {
                outcome.Ok = true;
                return outcome;
            }
            // Вернуть прежнее тем, у кого материал сменился; не вставшие не менялись.
            for (int i = 0; i < done.Count; i++)
            {
                if (missed.Contains(done[i])) continue;
                bool back;
                try
                {
                    back = restore(done[i], before[i]);
                }
                catch (Exception ex)
                {
                    Log.Error("Материал по геометрии: возврат материала тела " + name(done[i]), ex);
                    back = false;
                }
                if (!back) outcome.Stuck.Add(name(done[i]));
            }
            return outcome;
        }
    }
}
