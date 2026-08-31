// Договор модулей: порт module.rs. run(world, host) — один шаг;
// copy_on_* — механика самоконструкции CX3D.

using System.Collections.Generic;

namespace Prometheus.Runtime
{
    public readonly struct Host
    {
        public readonly int Soma;        // cell index; -1 если нейрит
        public readonly int NeuriteCell; // (cell, idx); валидно при IsNeurite
        public readonly int NeuriteIdx;
        public readonly bool IsNeurite;

        Host(int soma, int cell, int idx, bool isNeurite)
        {
            Soma = soma; NeuriteCell = cell; NeuriteIdx = idx; IsNeurite = isNeurite;
        }

        public static Host OfSoma(int cell) => new Host(cell, -1, -1, false);
        public static Host OfNeurite(int cell, int idx) => new Host(-1, cell, idx, true);
    }

    public interface IModule
    {
        void Run(World world, in Host host);
        void FinishStep(World world, in Host host) { }
        bool CopyOnDivide { get; }
        bool CopyOnBranch { get; }
        bool CopyOnElongate { get; }
        IModule BoxedCopy();
    }

    public interface ICellModule
    {
        void Run(World world, int cell);
        bool CopyOnDivide { get; }
        ICellModule BoxedCopy();
    }

    /// Отложенные эффекты параллельного цикла (BioDynaMo Behavior).
    public struct DeferredOp
    {
        public enum OpKind { Secretion, Division }
        public OpKind Kind;
        public int SubstanceIdx;
        public int GridIdx;
        public double Amount;
        public int Cell;
    }
}
