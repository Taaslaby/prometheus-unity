// CX3D-модули (порт modules.rs): цикл деления, хемоаттракция/репульсия,
// секреция, миграция, фасцикуляция, апоптоз, ван Пелт, NETMORPH-рост, etc.

using System;
using System.Linq;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime.Modules
{
    /// Деление при достижении диаметра (CX3D SimpleCellCycle).
    public class SimpleCellCycle : ICellModule
    {
        public double DVolDt;
        public double MinDiameter;

        public SimpleCellCycle(double dVolDt, double minDiameter)
        {
            DVolDt = dVolDt;
            MinDiameter = minDiameter;
        }

        public void Run(World w, int cell)
        {
            if (w.Cells[cell].Soma.Radius * 2.0 < MinDiameter)
                w.GrowSoma(cell, DVolDt);
            else
                w.RequestDivision(cell);
        }

        public bool CopyOnDivide => true;
        public ICellModule BoxedCopy() => new SimpleCellCycle(DVolDt, MinDiameter);
    }

    /// Ровно одно деление (1 → 2 клетки).
    public class DivideOnce : ICellModule
    {
        public double DVolDt;
        public double MinDiameter;
        bool _divided;

        public DivideOnce(double dVolDt, double minDiameter)
        {
            DVolDt = dVolDt;
            MinDiameter = minDiameter;
        }

        public void Run(World w, int cell)
        {
            if (_divided) return;
            if (w.Cells[cell].Soma.Radius * 2.0 < MinDiameter)
                w.GrowSoma(cell, DVolDt);
            else
            {
                w.RequestDivision(cell);
                _divided = true;
            }
        }

        public bool CopyOnDivide => false;
        public ICellModule BoxedCopy() => new DivideOnce(DVolDt, MinDiameter);
    }

    /// Судьба из бина концентрации (Wolpert positional information).
    public class Differentiate : ICellModule
    {
        public string Substance;
        public double BinWidth;

        public Differentiate(string substance, double binWidth)
        {
            Substance = substance;
            BinWidth = binWidth;
        }

        public void Run(World w, int cell)
        {
            double c = w.Concentration(Substance, w.Cells[cell].Soma.Pos);
            w.Cells[cell].Role = (int)System.Math.Floor(c / BinWidth);
        }

        public bool CopyOnDivide => true;
        public ICellModule BoxedCopy() => new Differentiate(Substance, BinWidth);
    }

    /// Ограничение угла поворота конуса (CX3D turnRate).
    static class TurnClamp
    {
        public static Vec3 ClampTurn(Vec3 prev, Vec3 dir, double? maxDeg)
        {
            if (maxDeg == null) return dir;
            double dot = Vec3.Clamp(prev.Dot(dir), -1, 1);
            double ang = Math2.Acos(dot);
            double limit = maxDeg.Value * Math2.PI / 180.0;
            if (ang <= limit) return dir;
            var axis = prev.Cross(dir);
            if (axis.Len() <= 1e-12) return prev;
            return Math2.RotateVec(dir, axis.Normalized(), -(ang - limit));
        }
    }

    /// Ростовой конус по градиенту морфогена (CX3D NeuriteChemoAttraction).
    public class ChemoAttraction : IModule
    {
        public string Substance;
        public double BranchingFactor;
        public double Speed = 1.0;
        public double OldDirWeight = 1.0;
        public double GradientWeight = 0.2;
        public double NoiseWeight = 0.6;
        public double StopConcentration = 0.3;
        public double Repulsion;
        public double? MaxLenUm;
        public double? TurnRateDeg;
        Vec3? _direction;

        public ChemoAttraction(string substance) { Substance = substance; }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            if (MaxLenUm != null && neur.LengthUm() >= MaxLenUm) return;
            var tip = neur.Tip();
            double conc = w.Concentration(Substance, tip);
            var grad = w.Gradient(Substance, tip);

            if (conc > StopConcentration) return;

            var old = _direction ?? grad.Normalized();
            var noise = w.RandomVec(NoiseWeight);
            var crowd = Repulsion > 0 ? w.CrowdingForce(tip, 10.0, Repulsion, cell) : Vec3.Zero;
            var stepDir = old * OldDirWeight + grad.Normalized() * GradientWeight + noise + crowd;
            stepDir = TurnClamp.ClampTurn(old, stepDir.Normalized(), TurnRateDeg);

            w.ExtendNeurite(cell, idx, stepDir * Speed);
            _direction = stepDir;

            double branchProb = Vec3.Clamp(conc * BranchingFactor, 0, 0.05);
            if (w.Rand() < branchProb) w.BranchNeurite(cell, idx);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new ChemoAttraction(Substance)
        {
            BranchingFactor = BranchingFactor,
            Speed = Speed,
            OldDirWeight = OldDirWeight,
            GradientWeight = GradientWeight,
            NoiseWeight = NoiseWeight,
            StopConcentration = StopConcentration,
            Repulsion = Repulsion,
            MaxLenUm = MaxLenUm,
            TurnRateDeg = TurnRateDeg,
        };
    }

    /// Конус к фиксированной точке (identity-directed guidance).
    public class PointAttraction : IModule
    {
        public Vec3 Target;
        public double Speed;
        public double OldDirWeight = 0.4;
        public double NoiseWeight = 0.1;
        public double StopDistance;
        Vec3? _direction;

        public PointAttraction(Vec3 target, double speed)
        {
            Target = target;
            Speed = speed;
        }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var tip = w.Cells[cell].Neurites[idx].Tip();
            var to = Target - tip;
            double d = to.Len();
            if (d <= StopDistance) return;
            var grad = to * (1.0 / System.Math.Max(d, 1e-12));
            var old = _direction ?? grad;
            var noise = NoiseWeight > 0 ? w.RandomVec(NoiseWeight) : Vec3.Zero;
            var stepDir = (old * OldDirWeight + grad + noise).Normalized();
            w.ExtendNeurite(cell, idx, stepDir * Speed);
            _direction = stepDir;
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new PointAttraction(Target, Speed)
        {
            OldDirWeight = OldDirWeight,
            NoiseWeight = NoiseWeight,
            StopDistance = StopDistance,
        };
    }

    /// Секреция морфогена сомой.
    public class Secretor : IModule
    {
        public string Substance;
        public double Amount;

        public Secretor(string substance, double amount)
        {
            Substance = substance;
            Amount = amount;
        }

        public void Run(World w, in Host host)
        {
            if (host.IsNeurite) return; // Secretor — сомный модуль
            int cell = host.Soma;
            w.AddSubstanceAt(Substance, w.Cells[cell].Soma.Pos, Amount);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => true;
        public bool CopyOnBranch => false;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new Secretor(Substance, Amount);
    }

    /// Астроцит: секреция TSP/Sparc + буст соседних синапсов.
    public class Astrocyte : IModule
    {
        public string Substance;
        public double Amount;
        public double Radius = 15.0;
        public double WeightBoost = 0.1;

        public Astrocyte(string substance, double amount)
        {
            Substance = substance;
            Amount = amount;
        }

        public void Run(World w, in Host host)
        {
            if (host.IsNeurite) return; // Astrocyte — сомный модуль
            int cell = host.Soma;
            if (w.Cells[cell].CellType != CellType.Glia) return;
            var pos = w.Cells[cell].Soma.Pos;
            w.AddSubstanceAt(Substance, pos, Amount);
            double conc = w.Concentration(Substance, pos);
            double boost = System.Math.Min(conc * WeightBoost, 2.0);
            for (int s = 0; s < w.Synapses.Count; s++)
            {
                var syn = w.Synapses[s];
                if (pos.Dist(w.Cells[syn.Post].Soma.Pos) < Radius)
                {
                    syn.Weight = System.Math.Min(syn.Weight + boost, 100.0);
                    w.Synapses[s] = syn;
                }
            }
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => false;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new Astrocyte(Substance, Amount)
        {
            Radius = Radius,
            WeightBoost = WeightBoost,
        };
    }

    /// Hillman/L-Neuron: ветвление по диаметру + taper.
    public class BranchOnDiameter : IModule
    {
        public double TaperRate;
        public double BifurcationThreshold;
        public double RallPower = 1.5;
        public double Speed = 1.0;
        public double BranchAngle = Math2.PI / 3;
        public BalancedForces? Balanced;
        bool _stopped;

        public BranchOnDiameter(double taperRate, double bifurcationThreshold)
        {
            TaperRate = taperRate;
            BifurcationThreshold = bifurcationThreshold;
        }

        public void Run(World w, in Host host)
        {
            if (_stopped || !host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var dir = w.Cells[cell].Neurites[idx].Axis();
            w.ExtendNeurite(cell, idx, dir * Speed);
            double d = w.Cells[cell].Neurites[idx].Diameter;
            double newD = System.Math.Max(d - TaperRate * Speed, 0.01);
            w.Cells[cell].Neurites[idx].Diameter = newD;
            if (newD > BifurcationThreshold)
            {
                if (Balanced != null)
                    w.BifurcateNeuriteBalanced(cell, idx, RallPower, 0, 0, Balanced.Value);
                else
                    w.BifurcateNeurite(cell, idx, BranchAngle, RallPower);
            }
            _stopped = true;
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new BranchOnDiameter(TaperRate, BifurcationThreshold)
        {
            RallPower = RallPower,
            Speed = Speed,
            BranchAngle = BranchAngle,
            Balanced = Balanced,
        };
    }

    /// NETMORPH van Pelt branching (TSBM).
    public class VanPeltBranching : IModule
    {
        public double BInf, Tau, E, S;
        public double BranchAngle = Math2.PI / 3;
        public double RallPower = 1.5;
        public double Speed = 1.0;
        public double MaxProb = 0.05;
        public BalancedForces? Balanced;
        bool _stopped;

        public VanPeltBranching(double bInf, double tau, double e, double s)
        {
            BInf = bInf; Tau = tau; E = e; S = s;
        }

        public void Run(World w, in Host host)
        {
            if (_stopped || !host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            var dir = neur.Axis();
            w.ExtendNeurite(cell, idx, dir * Speed);

            bool isAxon = neur.IsAxon;
            double nI = 0, cnSum = 0;
            foreach (var n in w.Cells[cell].Neurites)
                if (n.IsAxon == isAxon && n.IsTerminal)
                {
                    nI += 1;
                    cnSum += Math2.Pow(2.0, -S * n.BranchOrder);
                }
            double gamma = neur.BranchOrder;
            double p = VanPeltProbability(nI, gamma, w.Time, w.Dt, cnSum, BInf, Tau, E, S);
            p = Vec3.Clamp(p, 0, MaxProb);
            if (w.Rand() < p)
            {
                if (Balanced != null)
                    w.BifurcateNeuriteBalanced(cell, idx, RallPower, 0, 0, Balanced.Value);
                else
                    w.BifurcateNeurite(cell, idx, BranchAngle, RallPower);
                _stopped = true;
            }
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new VanPeltBranching(BInf, Tau, E, S)
        {
            BranchAngle = BranchAngle,
            RallPower = RallPower,
            Speed = Speed,
            MaxProb = MaxProb,
            Balanced = Balanced,
        };

        public static double VanPeltProbability(double nI, double gamma, double t, double dt,
            double cnSum, double bInf, double tau, double e, double s)
        {
            double cn = nI / cnSum;
            return Math2.Pow(nI, -e) * bInf * Math2.Exp(-t / tau)
                 * (Math2.Exp(dt / tau) - 1.0)
                 * Math2.Pow(2.0, -s * gamma) * cn;
        }
    }

    /// NETMORPH van Pelt arbor elongation (равная доля ресурса).
    public class VanPeltElongation : IModule
    {
        public double Nu0, F;

        public VanPeltElongation(double nu0, double f) { Nu0 = nu0; F = f; }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            double n = 0;
            foreach (var x in w.Cells[cell].Neurites)
                if (x.IsAxon == neur.IsAxon && x.IsTerminal) n += 1;
            double rate = Nu0 * Math2.Pow(n, -F) * w.Dt;
            var dir = neur.Axis();
            w.ExtendNeurite(cell, idx, dir * rate);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new VanPeltElongation(Nu0, F);
    }

    /// NETMORPH second-order terminal elongation (асимметричный сплит).
    public class SecondOrderElongation : IModule
    {
        public double Nu0, F, Inertia;
        public double? SpeedConstraint;
        public double Acceleration = 0.5; // NETMORPH default

        const double TsemStd = 0.3;

        public SecondOrderElongation(double nu0, double f, double inertia)
        {
            Nu0 = nu0; F = f; Inertia = inertia;
        }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            double dt = w.Dt;
            Acceleration += (dt / Inertia) * TsemStd * Normal01(w);
            Acceleration = Vec3.Clamp(Acceleration, -1, 1);
            double speed = w.Cells[cell].Neurites[idx].ElongationSpeed + Acceleration * dt;
            if (speed < 0) speed = 0;
            if (SpeedConstraint != null && speed > SpeedConstraint) speed = SpeedConstraint.Value;
            w.Cells[cell].Neurites[idx].ElongationSpeed = speed;
        }

        public void FinishStep(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            if (!neur.IsTerminal) return;
            double dt = w.Dt;
            double speed = neur.ElongationSpeed;
            bool isAxon = neur.IsAxon;
            double sum = 0, n = 0;
            foreach (var x in w.Cells[cell].Neurites)
                if (x.IsAxon == isAxon && x.IsTerminal) { sum += x.ElongationSpeed; n += 1; }
            double share = sum > 0 ? speed / sum : 0;
            double resource = Nu0 * Math2.Pow(n, 1.0 - F) * dt;
            var dir = neur.Axis();
            w.ExtendNeurite(cell, idx, dir * (share * resource));
        }

        static double Normal01(World w)
        {
            double u1 = System.Math.Max(w.Rand(), 1e-12);
            double u2 = w.Rand();
            return Math2.Sqrt(-2.0 * Math2.Ln(u1)) * Math2.Cos(Math2.TAU * u2);
        }

        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new SecondOrderElongation(Nu0, F, Inertia)
        {
            SpeedConstraint = SpeedConstraint,
            Acceleration = Acceleration,
        };
    }

    /// Полный NETMORPH growth loop в одном модуле.
    public class NetmorphGrowth : IModule
    {
        public double Nu0, F, Inertia;
        public double? SpeedConstraint;
        public double BInf, Tau, E, S;
        public double MaxProb = 0.05;
        public double RallPower = 1.5;
        public double Step = 1.0;
        public double EriStd;
        public BalancedForces Branch = BalancedForces.NetmorphDefaults;
        public TensionSteering? Steering;
        public TurnModel Turning = TurnModel.DefaultLinear;
        public double Acceleration = 0.5;
        bool _stopped;

        const double TsemStd = 0.3;

        public NetmorphGrowth(double nu0, double f, double inertia,
            double bInf, double tau, double e, double s)
        {
            Nu0 = nu0; F = f; Inertia = inertia;
            BInf = bInf; Tau = tau; E = e; S = s;
        }

        public NetmorphGrowth Tension(double historyPower, double turnRate) =>
            TensionFull(historyPower, turnRate, 0.0, 0.35);

        public NetmorphGrowth TensionFull(double historyPower, double turnRate,
            double veerMin, double veerMax)
        {
            Steering = new TensionSteering
            {
                HistoryPower = historyPower,
                VeerMin = veerMin,
                VeerMax = veerMax,
            };
            Turning = TurnModel.LinearRate(turnRate);
            return this;
        }

        public void Run(World w, in Host host)
        {
            if (_stopped || !host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            double dt = w.Dt;

            Acceleration += (dt / Inertia) * TsemStd * Normal01(w);
            Acceleration = Vec3.Clamp(Acceleration, -1, 1);
            double speed = neur.ElongationSpeed + Acceleration * dt;
            if (speed < 0) speed = 0;
            if (SpeedConstraint != null && speed > SpeedConstraint) speed = SpeedConstraint.Value;
            neur.ElongationSpeed = speed;

            bool isAxon = neur.IsAxon;
            double n = 0, cnSum = 0;
            foreach (var x in w.Cells[cell].Neurites)
                if (x.IsAxon == isAxon && x.IsTerminal)
                {
                    n += 1;
                    cnSum += Math2.Pow(2.0, -S * x.BranchOrder);
                }
            double gamma = neur.BranchOrder;
            double p = VanPeltBranching.VanPeltProbability(n, gamma, w.Time, dt, cnSum, BInf, Tau, E, S);
            p = Vec3.Clamp(p, 0, MaxProb);
            if (w.Rand() < p)
            {
                w.BifurcateNeuriteGrowth(cell, idx, RallPower, Step, EriStd, Branch);
                _stopped = true;
            }
        }

        public void FinishStep(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            if (!neur.IsTerminal) return;
            double dt = w.Dt;
            double speed = neur.ElongationSpeed;
            bool isAxon = neur.IsAxon;
            double sum = 0, n = 0;
            foreach (var x in w.Cells[cell].Neurites)
                if (x.IsAxon == isAxon && x.IsTerminal) { sum += x.ElongationSpeed; n += 1; }
            double share = sum > 0 ? speed / sum : 0;
            double resource = Nu0 * Math2.Pow(n, 1.0 - F) * dt;
            Vec3 dir;
            if (Steering != null)
            {
                var steering = Steering.Value;
                var turning = Turning;
                if (w.TurnEvent(ref turning, resource, 0.0))
                {
                    var geom = steering.Predict(neur);
                    dir = geom.Len() <= 1e-12 ? neur.Axis() : steering.Direction(neur, w.Rng);
                }
                else dir = neur.Axis();
            }
            else dir = neur.Axis();
            w.ExtendNeurite(cell, idx, dir * (share * resource));
        }

        static double Normal01(World w)
        {
            double u1 = System.Math.Max(w.Rand(), 1e-12);
            double u2 = w.Rand();
            return Math2.Sqrt(-2.0 * Math2.Ln(u1)) * Math2.Cos(Math2.TAU * u2);
        }

        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new NetmorphGrowth(Nu0, F, Inertia, BInf, Tau, E, S)
        {
            SpeedConstraint = SpeedConstraint,
            MaxProb = MaxProb,
            RallPower = RallPower,
            Step = Step,
            EriStd = EriStd,
            Branch = Branch,
            Steering = Steering,
            Turning = Turning,
            Acceleration = Acceleration,
        };
    }

    /// Конус, бегущий ОТ источника (CX3D ChemoRepulsion).
    public class ChemoRepulsion : IModule
    {
        public string Substance;
        public double Speed = 1.0;
        public double OldDirWeight = 1.0;
        public double GradientWeight = 0.2;
        public double NoiseWeight = 0.6;
        public double MinConcentration = 0.01;
        public double? MaxLenUm;
        public double? TurnRateDeg;
        Vec3? _direction;

        public ChemoRepulsion(string substance) { Substance = substance; }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            if (MaxLenUm != null && neur.LengthUm() >= MaxLenUm) return;
            var tip = neur.Tip();
            double conc = w.Concentration(Substance, tip);
            var grad = w.Gradient(Substance, tip);
            if (conc < MinConcentration) grad = Vec3.Zero;

            var old = _direction ?? grad.Normalized();
            var noise = w.RandomVec(NoiseWeight);
            var stepDir = old * OldDirWeight - grad.Normalized() * GradientWeight + noise;
            stepDir = TurnClamp.ClampTurn(old, stepDir.Normalized(), TurnRateDeg);
            w.ExtendNeurite(cell, idx, stepDir * Speed);
            _direction = stepDir;
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new ChemoRepulsion(Substance)
        {
            Speed = Speed,
            OldDirWeight = OldDirWeight,
            GradientWeight = GradientWeight,
            NoiseWeight = NoiseWeight,
            MinConcentration = MinConcentration,
            MaxLenUm = MaxLenUm,
            TurnRateDeg = TurnRateDeg,
        };
    }

    /// Ретракция конуса при слабом морфогене (CX3D RetractNeurite).
    public class RetractNeurite : IModule
    {
        public string Substance;
        public double Threshold;
        public double Rate;

        public RetractNeurite(string substance, double threshold, double rate)
        {
            Substance = substance;
            Threshold = threshold;
            Rate = rate;
        }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            var tip = w.Cells[host.NeuriteCell].Neurites[host.NeuriteIdx].Tip();
            if (w.Concentration(Substance, tip) < Threshold)
                w.RetractNeurite(host.NeuriteCell, host.NeuriteIdx, Rate);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => true;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new RetractNeurite(Substance, Threshold, Rate);
    }

    /// Миграция сомы по градиенту (CX3D Migration).
    public class SomaMigration : IModule
    {
        public string Substance;
        public double Speed;
        public bool Attract;

        public SomaMigration(string substance, double speed, bool attract)
        {
            Substance = substance;
            Speed = speed;
            Attract = attract;
        }

        public void Run(World w, in Host host)
        {
            if (host.IsNeurite) return;
            int cell = host.Soma;
            var grad = w.Gradient(Substance, w.Cells[cell].Soma.Pos);
            if (grad.Len() <= 1e-12) return;
            var dir = Attract ? grad.Normalized() : -grad.Normalized();
            w.MoveSoma(cell, dir * Speed);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => true;
        public bool CopyOnBranch => false;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new SomaMigration(Substance, Speed, Attract);
    }

    /// Фасцикуляция: пучкование с чужими нейритами.
    public class Fasciculation : IModule
    {
        public double Radius;
        public double Weight = 0.6;
        public double Speed = 1.0;
        public double OldDirWeight = 1.0;
        public double NoiseWeight = 0.1;
        public double? TurnRateDeg;
        public double? MaxLenUm;
        Vec3? _direction;

        public Fasciculation(double radius) { Radius = radius; }

        public void Run(World w, in Host host)
        {
            if (!host.IsNeurite) return;
            int cell = host.NeuriteCell, idx = host.NeuriteIdx;
            var neur = w.Cells[cell].Neurites[idx];
            if (MaxLenUm != null && neur.LengthUm() >= MaxLenUm) return;
            var tip = neur.Tip();
            var nearby = w.NearbyNeuriteDir(cell, tip, Radius);
            var bundle = nearby?.Item1;
            var old = _direction ?? bundle ?? Vec3.Zero;
            var noise = w.RandomVec(NoiseWeight);
            var steer = bundle ?? old;
            var stepDir = old * OldDirWeight + steer * Weight + noise;
            stepDir = TurnClamp.ClampTurn(old, stepDir.Normalized(), TurnRateDeg);
            w.ExtendNeurite(cell, idx, stepDir * Speed);
            _direction = stepDir;
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => true;
        public bool CopyOnElongate => true;
        public IModule BoxedCopy() => new Fasciculation(Radius)
        {
            Weight = Weight,
            Speed = Speed,
            OldDirWeight = OldDirWeight,
            NoiseWeight = NoiseWeight,
            TurnRateDeg = TurnRateDeg,
            MaxLenUm = MaxLenUm,
        };
    }

    /// Апоптоз при слабом тропическом факторе (CX3D CellApoptosis).
    public class CellApoptosis : IModule
    {
        public string Substance;
        public double Threshold;

        public CellApoptosis(string substance, double threshold)
        {
            Substance = substance;
            Threshold = threshold;
        }

        public void Run(World w, in Host host)
        {
            if (host.IsNeurite) return;
            int cell = host.Soma;
            if (w.Cells[cell].Alive &&
                w.Concentration(Substance, w.Cells[cell].Soma.Pos) < Threshold)
                w.KillCell(cell);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => true;
        public bool CopyOnBranch => false;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new CellApoptosis(Substance, Threshold);
    }

    /// Delta–Notch латеральное торможение (Collier 1996).
    public class LateralInhibition : ICellModule
    {
        public double Radius;
        public double V = 10.0;
        public double B = 100.0;
        public double Hill = 2.0;
        public double Alpha = 0.1;
        public double NotchThreshold = 0.3;
        public int PrimaryRole = 1;

        public LateralInhibition(double radius) { Radius = radius; }

        public void Run(World w, int cell)
        {
            double dt = System.Math.Min(w.Dt, 0.1);
            var neighbours = w.NeighborsWithin(cell, Radius);
            double meanD = 0;
            if (neighbours.Count > 0)
            {
                foreach (var nb in neighbours) meanD += w.Cells[nb].Delta;
                meanD /= neighbours.Count;
            }
            double n = w.Cells[cell].Notch;
            double d = w.Cells[cell].Delta;
            double dProd = V / (1.0 + B * Math2.Pow(n, Hill));
            double nProd = Math2.Pow(meanD, Hill) / (Alpha + Math2.Pow(meanD, Hill));
            w.Cells[cell].Delta = System.Math.Max(d + dt * (dProd - d), 0);
            w.Cells[cell].Notch = System.Math.Max(n + dt * (nProd - n), 0);
            w.Cells[cell].Role =
                w.Cells[cell].Notch < NotchThreshold && w.Cells[cell].Delta > 2.0 ? PrimaryRole : 0;
        }

        public bool CopyOnDivide => true;
        public ICellModule BoxedCopy() => new LateralInhibition(Radius)
        {
            V = V, B = B, Hill = Hill, Alpha = Alpha,
            NotchThreshold = NotchThreshold, PrimaryRole = PrimaryRole,
        };
    }

    /// Нейротрофическая конкуренция (Oppenheim 1991).
    public class TrophicCompetition : IModule
    {
        public string Substance;
        public double ConsumeRate = 0.02;
        public double MinUptake = 1e-4;
        public double Grace = 20.0;
        double _age;

        public TrophicCompetition(string substance) { Substance = substance; }

        public void Run(World w, in Host host)
        {
            if (host.IsNeurite) return;
            int cell = host.Soma;
            _age += w.Dt;
            var pos = w.Cells[cell].Soma.Pos;
            double c = w.Concentration(Substance, pos);
            double take = ConsumeRate * c * w.Dt;
            w.ConsumeSubstanceAt(Substance, pos, take);
            if (_age > Grace && take < MinUptake) w.KillCell(cell);
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => true;
        public bool CopyOnBranch => false;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new TrophicCompetition(Substance)
        {
            ConsumeRate = ConsumeRate,
            MinUptake = MinUptake,
            Grace = Grace,
        };
    }

    /// Метаморфоз: экдизоновый импульс — прореживание + взрослые нейроны.
    public class Metamorphosis : IModule
    {
        public double PruneFraction;
        public int AdultBornPerStep;
        public int PupalSteps;
        bool _pulseFired;
        int _step;

        public Metamorphosis(double pruneFraction, int adultBorn, int pupalSteps)
        {
            PruneFraction = pruneFraction;
            AdultBornPerStep = adultBorn;
            PupalSteps = pupalSteps;
        }

        public void Run(World w, in Host host)
        {
            if (host.IsNeurite) return;
            if (_step >= PupalSteps) return;
            _step++;

            if (!_pulseFired && _step == 1)
            {
                _pulseFired = true;
                if (PruneFraction > 0 && w.Synapses.Count > 0)
                {
                    int nPrune = (int)(w.Synapses.Count * PruneFraction);
                    var indexed = w.Synapses
                        .Select((s, i) => (i, s.Weight))
                        .OrderBy(x => x.Item2)
                        .Take(nPrune)
                        .Select(x => x.i)
                        .OrderByDescending(i => i)
                        .ToList();
                    foreach (var idx in indexed)
                    {
                        w.Synapses[idx] = w.Synapses[w.Synapses.Count - 1];
                        w.Synapses.RemoveAt(w.Synapses.Count - 1);
                    }
                }
            }

            if (_pulseFired && AdultBornPerStep > 0)
            {
                var center = new Vec3(
                    (w.Bounds_.Min.X + w.Bounds_.Max.X) * 0.5,
                    (w.Bounds_.Min.Y + w.Bounds_.Max.Y) * 0.5,
                    (w.Bounds_.Min.Z + w.Bounds_.Max.Z) * 0.5);
                var span = w.Bounds_.Max - w.Bounds_.Min;
                for (int i = 0; i < AdultBornPerStep; i++)
                {
                    var d = new Vec3(w.Rand() - 0.5, w.Rand() - 0.5, w.Rand() - 0.5).Normalized();
                    double r = 0.35 + w.Rand() * 0.15;
                    double maxDim = System.Math.Max(span.X, System.Math.Max(span.Y, span.Z));
                    var pos = center + d * (r * maxDim);
                    pos.X = Vec3.Clamp(pos.X, w.Bounds_.Min.X, w.Bounds_.Max.X);
                    pos.Y = Vec3.Clamp(pos.Y, w.Bounds_.Min.Y, w.Bounds_.Max.Y);
                    pos.Z = Vec3.Clamp(pos.Z, w.Bounds_.Min.Z, w.Bounds_.Max.Z);
                    w.AddCell(pos, CellType.Excitatory, 3.0);
                }
            }
        }

        public void FinishStep(World w, in Host host) { }
        public bool CopyOnDivide => false;
        public bool CopyOnBranch => false;
        public bool CopyOnElongate => false;
        public IModule BoxedCopy() => new Metamorphosis(PruneFraction, AdultBornPerStep, PupalSteps)
        {
            _pulseFired = _pulseFired,
            _step = _step,
        };
    }
}
