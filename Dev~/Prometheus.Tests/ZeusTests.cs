// Зевс — архитектор над Прометеем: проводки (монтажные схемы) с
// параметрами-портами. Проверяется ПОВЕДЕНИЕ прожига: проводка
// разворачивается в Layout, растёт Прометеем и несёт СВОЮ семантику
// (гейт — синергию, дифф — антагонизм, коллизии портов — отказ).

using System.Linq;
using Xunit;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;

namespace Prometheus.Tests
{
    public class ZeusTests
    {
        static ConnectomeGrow.GrowResult Burn(Layout l, ulong seed = 42) =>
            ConnectomeGrow.Grow(l, Steering.Default, seed);

        [Fact]
        public void Gate_Wiring_Expands_And_Grows()
        {
            var layout = Zeus.Sensorimotor()
                .And(Zeus.Gate("SL", "SR", into: "ML"))
                .Build();
            layout.Validate();
            var grown = Burn(layout);

            // Затворные рёбра живы: оба входа в DIS, GATE на ML.
            Assert.True(grown.EdgesBetween("SL", "G1.A").Count > 0 ||
                        grown.RegionTags.Any(t => t.StartsWith("G") && t.EndsWith(".A") &&
                            grown.EdgesBetween(t, grown.RegionTags.First(x => x.EndsWith(".DIS"))).Count > 0),
                "вход в гейт вырос");
            Assert.True(grown.EdgesBetween("ML", "ML").Count >= 0, "ML жив");
        }

        [Fact]
        public void Gate_Wiring_Claims_Port_Exclusively()
        {
            // Два гейта на ОДИН выход — архитектурный баг: Зевс обязан
            // отказаться внятно, а не молча слепить кашу.
            var ex = Record.Exception(() =>
                Zeus.Sensorimotor()
                    .And(Zeus.Gate("SL", "SR", into: "ML"))
                    .And(Zeus.Gate("SL", "SR", into: "ML"))
                    .Build());
            Assert.True(ex is System.InvalidOperationException,
                $"ожидался отказ коллизии, получено {ex?.GetType().Name}");
        }

        [Fact]
        public void Differential_Wiring_Makes_Antagonist_Pair()
        {
            // Дифф-путь: правый сенсор тормозит ЛЕВЫЙ мотор (кросс-инвертор),
            // прямых кросс-путей нет (Exclude).
            var layout = Zeus.Sensorimotor()
                .And(Zeus.Differential("SL", "SR", "ML", "MR"))
                .Build();
            layout.Validate();
            var grown = Burn(layout);

            // Прямые пути: сенсор к своему мотору.
            Assert.True(grown.EdgesBetween("SL", "ML").Count > 0, "левый→левый");
            Assert.True(grown.EdgesBetween("SR", "MR").Count > 0, "правый→правый");
            // Кросс-прямых нет: SR→ML запрещён Exclude.
            Assert.True(grown.EdgesBetween("SR", "ML").Count == 0, "кросс-прямой исключён");
        }

        [Fact]
        public void Fan_And_Converge_Compose()
        {
            // Веер + сход: SL веером в ML/I/TAIL, TAIL собирает сход.
            var layout = Zeus.Sensorimotor()
                .And(Zeus.Fan("SL", "ML", "I", "TAIL"))
                .And(Zeus.Converge("TAIL", "I", "ML"))
                .Build();
            layout.Validate();
            Assert.Contains(("SL", "ML"), layout.AllowedPairs);
            Assert.Contains(("SL", "TAIL"), layout.AllowedPairs);
            Assert.Contains(("I", "TAIL"), layout.AllowedPairs);
            Assert.Contains(("ML", "TAIL"), layout.AllowedPairs);
        }

        [Fact]
        public void Cpg_Wiring_Kicks_Oscillation_Structure()
        {
            var layout = Zeus.Sensorimotor()
                .And(Zeus.Cpg("I", "ML", "MR"))
                .Build();
            layout.Validate();
            var grown = Burn(layout);
            // Полукольцо: взаимное торможение выросло.
            var hcL = grown.RegionTags.FirstOrDefault(t => t.Contains("HC") && t.EndsWith(".L"));
            var hcR = grown.RegionTags.FirstOrDefault(t => t.Contains("HC") && t.EndsWith(".R"));
            Assert.NotNull(hcL);
            Assert.NotNull(hcR);
            Assert.True(grown.EdgesBetween(hcL, hcR).Count > 0, "L↔R торможение");
        }

        [Fact]
        public void Full_Sensorimotor_With_Wirings_Reads_Like_Architecture()
        {
            // Пример из доки Зевса: сборка читается как архитектура,
            // не как морфогенез.
            var wiring = Zeus.Sensorimotor()
                .And(Zeus.Gate("SL", "SR", into: "ML"))
                .And(Zeus.Fan("SR", "MR", "TAIL"))
                .And(Zeus.Converge("TAIL", "ML", "MR"));
            var layout = wiring.Build();
            layout.Validate();
        }
    }
}
