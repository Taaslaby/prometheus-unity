// QoL-надстройка над World: быстрые добавления клеток/нейритов/модулей
// без знания внутренностей, типизированные фабрики модулей.

using System.Collections.Generic;
using Prometheus.Runtime.Modules;

namespace Prometheus.Runtime
{
    public static class WorldExtensions
    {
        // ── клетки ─────────────────────────────────────────────────

        /// Добавить возбуждающую клетку.
        public static int AddExcitatory(this World w, Prometheus.Runtime.Math.Vec3 pos,
            double radius = 4.0) => w.AddCell(pos, CellType.Excitatory, radius);

        /// Добавить тормозную клетку.
        public static int AddInhibitory(this World w, Prometheus.Runtime.Math.Vec3 pos,
            double radius = 4.0) => w.AddCell(pos, CellType.Inhibitory, radius);

        /// Добавить клетку с тегом (для tagged-синаптогенеза).
        public static int AddTagged(this World w, string tag, Prometheus.Runtime.Math.Vec3 pos,
            CellType type, double radius = 4.0)
        {
            int id = w.AddCell(pos, type, radius);
            w.Cells[id].Tag = tag;
            return id;
        }

        /// Живые клетки.
        public static IEnumerable<Cell> AliveCells(this World w)
        {
            foreach (var c in w.Cells) if (c.Alive) yield return c;
        }

        /// Клетки по тегу.
        public static IEnumerable<Cell> CellsWithTag(this World w, string tag)
        {
            foreach (var c in w.Cells) if (c.Tag == tag) yield return c;
        }

        /// Средняя концентрация по живым сомам (диагностика морфогена).
        public static double MeanConcentration(this World w, string substance)
        {
            int n = 0; double sum = 0;
            foreach (var c in w.Cells)
            {
                if (!c.Alive) continue;
                sum += w.Concentration(substance, c.Soma.Pos);
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        /// Всего нейритов (удобный alias).
        public static int NeuriteCount(this World w) => w.TotalNeurites();

        // ── нейриты ───────────────────────────────────────────────

        /// Проращивание аксон-«хемо» в одну строку: аксон + ChemoAttraction.
        public static int GrowAxon(this World w, int cell, string targetMorphogen,
            double maxLen, SteeringLike pars)
        {
            int idx = w.NewNeurite(cell, true, 0.5);
            var chemo = new ChemoAttraction(targetMorphogen)
            {
                NoiseWeight = pars.NoiseWeight,
                GradientWeight = pars.GradientWeight,
                OldDirWeight = pars.OldDirWeight,
                TurnRateDeg = pars.TurnRateDeg,
                StopConcentration = pars.StopConcentration,
                MaxLenUm = maxLen,
                Speed = pars.Speed,
            };
            w.AddNeuriteModule(cell, idx, chemo);
            return idx;
        }

        /// Дендрит-паразит: нейрит без модулей (только геометрия для контактов).
        public static int GrowDendrite(this World w, int cell, double diameter = 0.5)
        {
            int idx = w.NewNeurite(cell, false, diameter);
            return idx;
        }

        // ── модули: типизированные фабрики-обёртки ─────────────────

        /// Хемоаттракция (дефолты CX3D-туториала, однообразно настраивается).
        public static ChemoAttraction Chemo(this World w, string substance) =>
            new ChemoAttraction(substance);

        /// Секреция морфогена сомой.
        public static Secretor Secrete(this World w, string substance, double amount = 0.06) =>
            new Secretor(substance, amount);

        /// Деление при диаметре (SimpleCellCycle).
        public static SimpleCellCycle CellCycle(this World w,
            double dVolDt = 300.0, double minDiameter = 12.0) =>
            new SimpleCellCycle(dVolDt, minDiameter);

        /// Ровно одно деление.
        public static DivideOnce DivideOnce(this World w,
            double dVolDt = 300.0, double minDiameter = 12.0) =>
            new DivideOnce(dVolDt, minDiameter);

        /// Судьба по бинам морфогена.
        public static Differentiate Differentiate(this World w, string substance,
            double binWidth = 1.0) => new Differentiate(substance, binWidth);

        /// Delta–Notch латеральное торможение.
        public static LateralInhibition LateralInhibition(this World w,
            double radius = 20.0) => new LateralInhibition(radius);

        /// Апоптоз при слабом морфогене.
        public static CellApoptosis Apoptosis(this World w, string substance,
            double threshold = 0.05) => new CellApoptosis(substance, threshold);
    }

    /// Параметры GrowAxon (объединяют Steering и точечные подстройки).
    public struct SteeringLike
    {
        public double NoiseWeight;
        public double GradientWeight;
        public double OldDirWeight;
        public double TurnRateDeg;
        public double StopConcentration;
        public double Speed;

        public static SteeringLike ChemoDefaults => new SteeringLike
        {
            NoiseWeight = 0.6,
            GradientWeight = 0.2,
            OldDirWeight = 1.0,
            TurnRateDeg = double.NaN, // unlimited
            StopConcentration = 0.3,
            Speed = 1.0,
        };

        public static SteeringLike Dioma => new SteeringLike
        {
            NoiseWeight = 0.05,
            GradientWeight = 1.2,
            OldDirWeight = 0.2,
            TurnRateDeg = 25.0,
            StopConcentration = 9.5,
            Speed = 1.0,
        };

        public SteeringLike WithNoise(double v)
        { var c = this; c.NoiseWeight = v; return c; }

        public SteeringLike WithSpeed(double v)
        { var c = this; c.Speed = v; return c; }
    }
}
