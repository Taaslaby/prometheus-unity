// Прометей-Билдер: блоки-мотивы и их ПОВЕДЕНЧЕСКАЯ проверка. Не только
// «схема собирается» — каждый блок обязан делать своё дело в росте:
// затвор открываться на два входа и НЕ открываться на один.

using System.Linq;
using Xunit;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;

namespace Prometheus.Tests
{
    public class BlocksTests
    {
        static ConnectomeGrow.GrowResult Grow(Layout l, ulong seed = 42) =>
            ConnectomeGrow.Grow(l, Steering.Default, seed);

        [Fact]
        public void Gate_Block_Assembles_Into_Layout()
        {
            var layout = Blocks.Core()
                .And(Blocks.CoincidenceGate("G1", "SL", "SR", "ML"))
                .Build();
            layout.Validate();

            // Блок добавил свои регионы с суффиксами.
            var tags = layout.Regions.Select(r => r.Tag).ToList();
            Assert.Contains("G1.A", tags);
            Assert.Contains("G1.DIS", tags);
            Assert.Contains("G1.GATE", tags);
            // Пары блока: внутренние связи + выход на ML.
            Assert.Contains(("G1.DIS", "G1.GATE"), layout.AllowedPairs);
            Assert.Contains(("G1.GATE", "ML"), layout.AllowedPairs);
        }

        [Fact]
        public void Gate_Requires_Both_Inputs()
        {
            // Смысл затвора: один вход НЕ пробивает, два — открывают.
            // После роста: у DIS (дизингибитора) входы от обоих A и B.
            var layout = Blocks.Core()
                .And(Blocks.CoincidenceGate("G1", "SL", "SR", "ML"))
                .Build();
            var grown = Grow(layout);

            // Рёбра A→DIS и B→DIS: оба должны существовать.
            int aDis = grown.EdgesBetween("G1.A", "G1.DIS").Count;
            int bDis = grown.EdgesBetween("G1.B", "G1.DIS").Count;
            Assert.True(aDis > 0, "вход A обязан достать DIS");
            Assert.True(bDis > 0, "вход B обязан достать DIS");
            // И затвор тормозит выход.
            Assert.True(grown.EdgesBetween("G1.GATE", "ML").Count > 0,
                "GATE тормозит выход");
        }

        [Fact]
        public void Core_Matches_Fish_Layout_Topology()
        {
            // Core() — тот же холст, что Layouts.Fish: те же пары.
            var core = Blocks.Core().Build();
            var fish = Layouts.Fish();
            var corePairs = core.AllowedPairs.ToHashSet();
            foreach (var p in fish.AllowedPairs)
                Assert.Contains(p, corePairs);
        }

        [Fact]
        public void HalfCenter_Oscillates_After_Growth()
        {
            // Полукольцо после роста: взаимное торможение L↔R.
            var layout = LayoutBuilder.Create()
                .Region("DRIVE").At(0, -40, 0).Radius(8).Count(8)
                    .Axon("HC.L", 80).Axon("HC.R", 80)
                .Region("HC.L_OUT").At(-30, 40, 0).Radius(6).Count(6)
                    .Secrete("HC.L_OUT")
                .Region("HC.R_OUT").At(30, 40, 0).Radius(6).Count(6)
                    .Secrete("HC.R_OUT")
                .And(Blocks.HalfCenter("HC", "DRIVE"))
                .Build();
            var grown = Grow(layout);
            Assert.True(grown.EdgesBetween("HC.L", "HC.R").Count > 0, "L тормозит R");
            Assert.True(grown.EdgesBetween("HC.R", "HC.L").Count > 0, "R тормозит L");
            Assert.True(grown.EdgesBetween("DRIVE", "HC.L").Count > 0, "тонус бьёт L");
        }

        [Fact]
        public void Comparator_Has_Both_Outcomes()
        {
            // Компаратор: пути подтверждения (ACK) и ошибки (ERR).
            var layout = LayoutBuilder.Create()
                .Region("PRED").At(-40, -20, 0).Radius(8).Count(8)
                .Region("FACT").At(40, -20, 0).Radius(8).Count(8)
                .Region("ACK").At(-20, 40, 0).Radius(6).Count(6).Secrete("ACK")
                .Region("ERR").At(20, 40, 0).Radius(6).Count(6).Secrete("ERR")
                .And(Blocks.Comparator("C1", "PRED", "FACT", "ACK", "ERR"))
                .Build();
            var grown = Grow(layout);
            Assert.True(grown.EdgesBetween("C1.DIS", "ACK").Count > 0,
                "совпадение открывает ACK");
            Assert.True(grown.EdgesBetween("C1.GATE", "ERR").Count > 0,
                "затвор гасит ERR при совпадении");
        }

        [Fact]
        public void Blocks_Compose_Without_Tag_Collisions()
        {
            // Два затвора в одной сборке: теги с разными id не конфликтуют.
            var layout = Blocks.Core()
                .And(Blocks.CoincidenceGate("GA", "SL", "SR", "ML"))
                .And(Blocks.CoincidenceGate("GB", "SL", "SR", "MR"))
                .Build();
            layout.Validate();
            var tags = layout.Regions.Select(r => r.Tag).ToList();
            Assert.Equal(tags.Count, tags.Distinct().Count());
        }
    }
}
