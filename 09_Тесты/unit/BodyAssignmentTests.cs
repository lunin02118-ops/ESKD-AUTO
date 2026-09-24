using System.Collections.Generic;
using ESKD.MaterialSync.Core;

namespace ESKD.Tests
{
    /// <summary>
    /// Материал телам позиции — всем или ни одному (сверка SW API 23.09.2026, №38). Раньше при отказе SolidWorks на
    /// третьем теле первые два оставались с новым материалом: в одной позиции списка вырезов — разные материалы. Тела здесь —
    /// подделки: имя, свой материал и ответ SolidWorks на назначение.
    /// </summary>
    public static class BodyAssignmentTests
    {
        private const int NoError = SwCodes.BodyMaterialNoError;

        private sealed class FakeBody
        {
            public string Name;
            public string Material = "";
            public int Answer = NoError;
            /// <summary>SolidWorks отвечает «успех», а материал не меняет (SolidWorks 2025, e2e T14).</summary>
            public bool Ignores;
            public bool RestoreFails;
        }

        private static BodyAssignmentOutcome Assign(List<FakeBody> bodies, string target)
        {
            return BodyAssignment.Apply(bodies,
                b => b.Name,
                b => b.Material,
                b =>
                {
                    if (b.Answer == NoError && !b.Ignores) b.Material = target;
                    return b.Answer;
                },
                done => done.FindAll(b => b.Material != target),
                (b, before) =>
                {
                    if (b.RestoreFails) return false;
                    b.Material = before;
                    return true;
                },
                SwCodes.BodyMaterialProblem);
        }

        public static void Test_All_bodies_get_material()
        {
            List<FakeBody> bodies = new List<FakeBody> { new FakeBody { Name = "Труба-1" }, new FakeBody { Name = "Труба-2" } };
            BodyAssignmentOutcome outcome = Assign(bodies, "Труба");
            Assert.IsTrue(outcome.Ok, "назначено");
            Assert.AreEqual("Труба", bodies[0].Material, "первое тело");
            Assert.AreEqual("Труба", bodies[1].Material, "второе тело");
        }

        public static void Test_Refusal_on_third_body_rolls_back_first_two()
        {
            List<FakeBody> bodies = new List<FakeBody>
            {
                new FakeBody { Name = "Труба-1", Material = "Лист" }, new FakeBody { Name = "Труба-2" },
                new FakeBody { Name = "Труба-3", Answer = SwCodes.BodyMaterialExternalReference }
            };
            BodyAssignmentOutcome outcome = Assign(bodies, "Труба");
            Assert.IsFalse(outcome.Ok, "не назначено");
            Assert.IsTrue(outcome.Failure.Contains("Труба-3") && outcome.Failure.Contains("другой детали"), outcome.Failure);
            Assert.AreEqual("Лист", bodies[0].Material, "первому телу вернули прежний свой материал");
            Assert.AreEqual("", bodies[1].Material, "второму — снова без своего материала");
            Assert.AreEqual(0, outcome.Stuck.Count, "вернули всем");
        }

        public static void Test_Material_not_applied_is_reported_and_others_rolled_back()
        {
            // «Успех» без материала (SolidWorks 2025): тело не изменилось, возвращать его не нужно; изменившееся — вернуть.
            List<FakeBody> bodies = new List<FakeBody> { new FakeBody { Name = "Труба-1" }, new FakeBody { Name = "Труба-2", Ignores = true } };
            BodyAssignmentOutcome outcome = Assign(bodies, "Труба");
            Assert.IsFalse(outcome.Ok, "не назначено");
            Assert.IsTrue(outcome.Failure.Contains("назначьте его этим телам вручную") && outcome.Failure.Contains("Труба-2"), outcome.Failure);
            Assert.AreEqual("", bodies[0].Material, "первому телу вернули прежнее");
            Assert.AreEqual(0, outcome.Stuck.Count, "вернули всем");
        }

        public static void Test_Body_that_cannot_be_restored_is_named()
        {
            List<FakeBody> bodies = new List<FakeBody>
            {
                new FakeBody { Name = "Труба-1", RestoreFails = true },
                new FakeBody { Name = "Труба-2", Answer = SwCodes.BodyMaterialRolledBack }
            };
            BodyAssignmentOutcome outcome = Assign(bodies, "Труба");
            Assert.IsFalse(outcome.Ok, "не назначено");
            Assert.AreEqual(1, outcome.Stuck.Count, "одно тело не вернулось");
            Assert.AreEqual("Труба-1", outcome.Stuck[0], "названо");
            Assert.IsTrue(outcome.Failure.Contains("полосу отката"), outcome.Failure);
        }
    }
}
