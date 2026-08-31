// NETMORPH модели роста: tension/radial направление, balanced-forces углы,
// turning models, Rall диаметры (порты direction/branch/turning/diameter_model.rs).

using System.Collections.Generic;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    /// NETMORPH tension direction model: сегменты истории тянут конус.
    public struct TensionSteering
    {
        public double HistoryPower; // default 2.0
        public double VeerMin;
        public double VeerMax;

        public static TensionSteering Default => new TensionSteering { HistoryPower = 2.0 };

        public Vec3 Predict(Neurite neurite)
        {
            var pts = neurite.Points;
            if (pts.Count < 2) return Vec3.Zero;
            var acc = Vec3.Zero;
            double distanceByFibre = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var seg = pts[i] - pts[i - 1];
                double l = seg.Len();
                if (l <= 0) continue;
                double radius = System.Math.Max(neurite.Diameter * 0.5, 1e-9);
                double mass = l * Math2.PI * radius * radius;
                double distance = distanceByFibre + l * 0.5;
                double pull = mass / Math2.Pow(distance, HistoryPower);
                acc = acc + seg.Normalized() * pull;
                distanceByFibre += l;
            }
            return acc;
        }

        public Vec3 Direction(Neurite neurite, ChaCha12 rng)
        {
            var predicted = Predict(neurite);
            if (predicted.Len() <= 1e-12) return neurite.Axis();
            var dir = predicted.Normalized();
            double veer = VeerMax > VeerMin ? rng.GenRange(VeerMin, VeerMax) : 0.0;
            if (System.Math.Abs(veer) <= 1e-12) return dir;
            var any = dir.X < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            var perp = dir.Cross(any).Normalized();
            double angle = rng.GenF64() * Math2.TAU;
            var tilt = dir * Math2.Cos(angle) + perp * Math2.Sin(angle);
            return Math2.RotateVec(dir, tilt, veer).Normalized();
        }
    }

    /// NETMORPH radial direction model (Samsonovich).
    public struct RadialSteering
    {
        public bool Samsonovich;

        public Vec3 Direction(Vec3 cellPos, Neurite neurite)
        {
            if (Samsonovich && neurite.Points.Count >= 2)
            {
                var rootDir = neurite.Points[1] - neurite.Points[0];
                if (rootDir.Len() > 1e-12) return rootDir.Normalized();
            }
            var radial = neurite.Tip() - cellPos;
            return radial.Len() <= 1e-12 ? neurite.Axis() : radial.Normalized();
        }
    }

    /// NETMORPH balanced-forces branch angles.
    public struct BalancedForces
    {
        public double ParallelogramMean;
        public double ParallelogramStd;
        public double MaxAngle;
        public double PerturbStd;

        public static BalancedForces NetmorphDefaults => new BalancedForces
        {
            ParallelogramMean = Math2.PI / 2,
            ParallelogramStd = 0.5,
            MaxAngle = Math2.PI - 0.1,
            PerturbStd = 0.3,
        };

        public (double, double) Angles(double force1, double force2, ChaCha12 rng)
        {
            double parallelogram = Vec3.Clamp(
                World.SampleNormal(rng, ParallelogramMean, ParallelogramStd), 0, MaxAngle);

            double f1 = force1, f2 = force2;
            if (f1 == 0 && f2 == 0)
            {
                f1 = rng.GenF64();
                f2 = rng.GenF64();
            }
            double a1, a2;
            if (f1 == 0) { a1 = parallelogram; a2 = 0; }
            else if (f2 == 0) { a1 = 0; a2 = parallelogram; }
            else
            {
                a1 = Math2.Atan(f2 / f1);
                a2 = parallelogram - a1;
            }

            double p1 = PerturbStd > 0
                ? 1.0 + Vec3.Clamp(World.SampleNormal(rng, 0, PerturbStd), -1, 1) : 1.0;
            double p2 = PerturbStd > 0
                ? 1.0 + Vec3.Clamp(World.SampleNormal(rng, 0, PerturbStd), -1, 1) : 1.0;
            return (a1 * p1, a2 * p2);
        }
    }

    /// NETMORPH turning models (TSTM).
    public enum TurnModelKind { None, EachDt, LinearRate, BranchCoupled }

    public struct TurnModel
    {
        public TurnModelKind Kind;
        public double TPrev;
        public double TurnRate;
        public double Tf;

        public static TurnModel LinearRate(double rate) =>
            new TurnModel { Kind = TurnModelKind.LinearRate, TurnRate = rate };
        public static TurnModel DefaultLinear => LinearRate(0.1);
        public static TurnModel EachDt => new TurnModel { Kind = TurnModelKind.EachDt };
        public static TurnModel BranchCoupled(double tf) =>
            new TurnModel { Kind = TurnModelKind.BranchCoupled, Tf = tf };

        public double PredictTurn(double elongatedLen, double pBranch)
        {
            switch (Kind)
            {
                case TurnModelKind.None: return -1.0;
                case TurnModelKind.EachDt: return 1.0;
                case TurnModelKind.LinearRate:
                    return System.Math.Min(TurnRate * elongatedLen, 1.0);
                case TurnModelKind.BranchCoupled:
                    return System.Math.Min((1.0 + Tf) * pBranch, 1.0);
                default: return -1.0;
            }
        }

        public static double Combined(double a, double wa, double b, double wb)
        {
            if (wa + wb <= 0) return a;
            return (a * wa + b * wb) / (wa + wb);
        }
    }

    /// NETMORPH Rall power-law диаметры (снизу вверх).
    public class RallPowerLaw
    {
        public const double DefaultEPowerMean = 1.47;
        public const double DefaultEPowerStd = 0.3;
        public const double DefaultDTermMean = 0.7;
        public const double DefaultDTermStd = 0.3;

        public double EPowerMean = DefaultEPowerMean;
        public double EPowerStd = DefaultEPowerStd;
        public double DTermMean = DefaultDTermMean;
        public double DTermStd = DefaultDTermStd;
        public double DMax = -1.0;

        public double SampleTerminal(ChaCha12 rng) =>
            System.Math.Abs(World.SampleNormal(rng, DTermMean, DTermStd));

        public double Combine(ChaCha12 rng, double d1, double d2)
        {
            double e = World.SampleNormal(rng, EPowerMean, EPowerStd);
            double combined = Math2.Pow(Math2.Pow(d1, e) + Math2.Pow(d2, e), 1.0 / e);
            return DMax > 0 && combined > DMax ? DMax : combined;
        }
    }

    public static class DiameterModel
    {
        public static void AssignRallDiameters(Cell cell, RallPowerLaw model, ChaCha12 rng)
        {
            for (int i = 0; i < cell.Neurites.Count; i++)
                if (cell.Neurites[i].Parent < 0)
                    AssignSubtree(cell, i, model, rng);
        }

        static double AssignSubtree(Cell cell, int idx, RallPowerLaw model, ChaCha12 rng)
        {
            var children = ChildrenOf(cell, idx);
            if (children.Count == 0)
            {
                double d = model.SampleTerminal(rng);
                cell.Neurites[idx].Diameter = d;
                return d;
            }
            if (children.Count == 1)
            {
                double d = AssignSubtree(cell, children[0], model, rng);
                cell.Neurites[idx].Diameter = d;
                return d;
            }
            double d1 = AssignSubtree(cell, children[0], model, rng);
            double d2 = AssignSubtree(cell, children[1], model, rng);
            double dd = model.Combine(rng, d1, d2);
            cell.Neurites[idx].Diameter = dd;
            return dd;
        }

        public static void AssignTopDown(Cell cell, double rallPower)
        {
            for (int i = 0; i < cell.Neurites.Count; i++)
                if (cell.Neurites[i].Parent < 0)
                    TopDownSubtree(cell, i, rallPower);
        }

        static double TopDownSubtree(Cell cell, int idx, double rallPower)
        {
            double d = cell.Neurites[idx].Diameter;
            double dChild = d * Math2.Pow(2.0, -1.0 / rallPower);
            foreach (var c in ChildrenOf(cell, idx))
            {
                cell.Neurites[c].Diameter = dChild;
                TopDownSubtree(cell, c, rallPower);
            }
            return d;
        }

        static List<int> ChildrenOf(Cell cell, int idx)
        {
            var res = new List<int>();
            for (int i = 0; i < cell.Neurites.Count; i++)
                if (cell.Neurites[i].Parent == idx) res.Add(i);
            return res;
        }
    }
}
