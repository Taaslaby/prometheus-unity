// Spatial hash (порт uniform_grid.rs): O(1) соседство.
//
// Ключ бакета — биективное кодирование Morton-подобной схемой:
// знаковые 21-битные координаты × 3 уложены в long без коллизий
// (XOR-сжатие давало ложные попадания на краях диапазона).

using System.Collections.Generic;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    public class UniformGrid
    {
        const int MaxCoord = 1_000_000;
        readonly double _cellSize;
        readonly double _invCell;
        Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();

        public UniformGrid(double cellSize)
        {
            if (cellSize <= 0 || double.IsNaN(cellSize) || double.IsInfinity(cellSize))
                throw new System.ArgumentException(
                    $"cellSize должен быть конечным > 0, получил {cellSize}", nameof(cellSize));
            _cellSize = cellSize;
            _invCell = 1.0 / cellSize;
        }

        (int, int, int) Key(Vec3 p)
        {
            int K(double x)
            {
                double v = System.Math.Floor(x * _invCell);
                if (double.IsNaN(v)) return 0;
                return (int)Vec3.Clamp(v, -MaxCoord, MaxCoord);
            }
            return (K(p.X), K(p.Y), K(p.Z));
        }

        /// Биективный ключ трёх знаковых координат: сдвиг в беззнаковый
        /// диапазон (+MaxCoord), затем 21 бит на координату.
        /// Отрицательные координаты НЕ должны проходить через (uint)x —
        /// знакорасширение затирало старшие поля (коллизии на отрицательных).
        static long BucketId(int x, int y, int z) =>
            ((long)(uint)(x + MaxCoord) << 42)
            | ((long)(uint)(y + MaxCoord) << 21)
            | (uint)(z + MaxCoord);

        public void Clear() => _buckets.Clear();

        public void Insert(int idx, Vec3 pos)
        {
            var (x, y, z) = Key(pos);
            long id = BucketId(x, y, z);
            if (!_buckets.TryGetValue(id, out var list))
            {
                list = new List<int>();
                _buckets[id] = list;
            }
            list.Add(idx);
        }

        public List<int> Query(Vec3 p, double radius)
        {
            if (radius < 0) throw new System.ArgumentException(
                $"radius >= 0, получил {radius}", nameof(radius));
            int r = (int)System.Math.Min(
                System.Math.Ceiling(radius * _invCell), MaxCoord);
            var (cx, cy, cz) = Key(p);
            var outList = new List<int>();
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        long id = BucketId(cx + dx, cy + dy, cz + dz);
                        if (_buckets.TryGetValue(id, out var v)) outList.AddRange(v);
                    }
            return outList;
        }
    }
}
