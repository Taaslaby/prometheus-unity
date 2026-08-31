// Клетка + нейриты + вещество: порт cell.rs + substance.rs.

using System.Collections.Generic;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    public enum CellType { Excitatory = 0, Inhibitory = 1, Glia = 2 }

    public enum SynapseKind { Excitatory, Inhibitory }

    public enum ExcrescenceKind { Bouton, Spine }

    public struct Excrescence
    {
        public ExcrescenceKind Kind;
        public Vec3 Pos;
        public Vec3 Dir;
        public double Length;
    }

    public class Soma
    {
        public Vec3 Pos;
        public double Radius;
        public List<IModule> Modules = new List<IModule>();
    }

    public class Neurite
    {
        public bool IsAxon;
        public double Diameter;
        public List<Vec3> Points = new List<Vec3>();
        public List<IModule> Modules = new List<IModule>();
        public Dictionary<string, double> Intracellular = new Dictionary<string, double>();
        public List<Excrescence> Excrescences = new List<Excrescence>();
        public uint BranchOrder;
        public int Parent = -1; // -1 = None
        public double ElongationSpeed = 1.0;
        public bool IsTerminal = true;

        public double LengthUm()
        {
            double s = 0;
            for (int i = 1; i < Points.Count; i++) s += Points[i - 1].Dist(Points[i]);
            return s;
        }

        public Vec3 Tip() => Points[Points.Count - 1];

        public Vec3 Axis()
        {
            int n = Points.Count;
            if (n >= 2) return (Points[n - 1] - Points[n - 2]).Normalized();
            return new Vec3(0, 0, 1);
        }
    }

    public class Cell
    {
        public int Id;
        public CellType CellType;
        public int Role;
        public int SubRole;
        public string Tag = "";
        public Soma Soma = new Soma();
        public List<Neurite> Neurites = new List<Neurite>();
        public List<ICellModule> Modules = new List<ICellModule>();
        public Dictionary<string, double> Membrane = new Dictionary<string, double>();
        public Dictionary<string, double> Intracellular = new Dictionary<string, double>();
        public bool Alive = true;
        public double Delta;
        public double Notch;
    }

    public enum BoundaryConditionKind { Dirichlet, Neumann, Open, Closed }

    public struct BoundaryCondition
    {
        public BoundaryConditionKind Kind;
        public double Value; // Dirichlet / Neumann

        public static BoundaryCondition Dirichlet(double v) =>
            new BoundaryCondition { Kind = BoundaryConditionKind.Dirichlet, Value = v };
        public static BoundaryCondition Neumann(double v) =>
            new BoundaryCondition { Kind = BoundaryConditionKind.Neumann, Value = v };
        public static BoundaryCondition Closed() =>
            new BoundaryCondition { Kind = BoundaryConditionKind.Closed };
    }

    public class Substance
    {
        public string Id;
        public double Diffusion;
        public double Degradation;
        public double[] Field;
        public List<ArtificialGradient> Gradients = new List<ArtificialGradient>();
        public double UpperThreshold = 1e15;
        public double LowerThreshold = 0.0;
        public BoundaryCondition Boundary = BoundaryCondition.Neumann(0);
        public double[] C2;

        public Substance(string id, double diffusion, double degradation, in Grid grid,
            List<ArtificialGradient> gradients)
        {
            Id = id;
            Diffusion = diffusion;
            Degradation = degradation;
            Field = new double[grid.Volume];
            C2 = new double[grid.Volume];
            if (gradients != null) Gradients = gradients;
        }

        public Substance WithThresholds(double lo, double hi)
        {
            LowerThreshold = lo; UpperThreshold = hi; return this;
        }

        public Substance WithBoundary(BoundaryCondition bc) { Boundary = bc; return this; }
    }

    /// Аналитический профиль (CX3D artificial gradient).
    public struct ArtificialGradient
    {
        public enum Kind { Gaussian, Linear }
        public Kind Type;
        public int Axis;
        public double Max, Mean, Sigma;   // gaussian
        public double Peak, Zero;        // linear

        public static ArtificialGradient Gaussian(int axis, double max, double mean, double sigma) =>
            new ArtificialGradient { Type = Kind.Gaussian, Axis = axis, Max = max, Mean = mean, Sigma = sigma };

        public static ArtificialGradient Linear(int axis, double max, double peak, double zero) =>
            new ArtificialGradient { Type = Kind.Linear, Axis = axis, Max = max, Peak = peak, Zero = zero };

        public double Value(Vec3 p)
        {
            if (Type == Kind.Gaussian)
            {
                double d = p.Axis(Axis);
                double e = (d - Mean) / Sigma;
                return Max * Math2.Exp(-0.5 * e * e);
            }
            else
            {
                double d = p.Axis(Axis);
                double lo = Peak > Zero ? Zero : Peak;
                double hi = Peak > Zero ? Peak : Zero;
                if (d <= lo || d >= hi) return 0.0;
                return Max * (d - Zero) / (Peak - Zero);
            }
        }

        public Vec3 Gradient(Vec3 p)
        {
            if (Type == Kind.Gaussian)
            {
                double d = p.Axis(Axis);
                double e = (d - Mean) / Sigma;
                double g = Max * Math2.Exp(-0.5 * e * e) * (-e / Sigma);
                return AxisVec(Axis, g);
            }
            else
            {
                double d = p.Axis(Axis);
                double lo = Peak > Zero ? Zero : Peak;
                double hi = Peak > Zero ? Peak : Zero;
                if (d <= lo || d >= hi) return Vec3.Zero;
                return AxisVec(Axis, Max / (Peak - Zero));
            }
        }

        static Vec3 AxisVec(int axis, double v) =>
            axis == 0 ? new Vec3(v, 0, 0) : axis == 1 ? new Vec3(0, v, 0) : new Vec3(0, 0, v);
    }
}
