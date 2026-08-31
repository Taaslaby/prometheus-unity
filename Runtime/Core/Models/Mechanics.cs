// BioDynaMo механика: адгезия + отталкивание + упругость (порт mechanics.rs).

using System;
using System.Collections.Generic;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    public class MechanicsParams
    {
        public double AdhesionStrength = 0.5;
        public double RepulsionStrength = 2.0;
        public double ElasticStrength = 0.3;
        public double ElasticEquilibrium = 0.8;
        public double MaxForce = 5.0;
        public double Damping = 0.1;
        public double AdhesionCutoff = 2.5;
        public double[,] AdhesionMatrix = NewDefaultMatrix();
        public bool Enabled = true;

        static double[,] NewDefaultMatrix()
        {
            var m = new double[3, 3];
            m[0, 0] = 1.0;              // Exc ↔ Exc
            m[1, 1] = 0.6;              // Inh ↔ Inh
            return m;
        }
    }

    public struct CellVelocity
    {
        public Vec3 V;
    }

    public static class Mechanics
    {
        /// Сила на одну клетку — публичный API уровня референса
        /// (`compute_mechanics`). Внутри роста не используется: StepMechanics
        /// считает то же самое по массивам и одним проходом по рёбрам.
        public static Vec3 ComputeMechanics(Vec3 pos, double radius, int cellType,
            List<Vec3> allPos, List<double> allRadii, List<int> allTypes, int skipIdx,
            List<(int, int)> edges, MechanicsParams p, double dt)
        {
            if (!p.Enabled) return Vec3.Zero;

            var totalForce = Vec3.Zero;
            int n = allPos.Count;
            // Пара за AdhesionCutoff·rSum не даёт ни отталкивания (для него
            // нужно перекрытие, а оно ближе), ни адгезии — её вклад ровно
            // ноль. Отсеиваем по КВАДРАТУ расстояния, чтобы sqrt считался
            // только для реально участвующих пар (на росте это 15..30%).
            for (int j = 0; j < n; j++)
            {
                if (j == skipIdx) continue;
                double rSum = radius + allRadii[j];
                var dir = pos - allPos[j];
                double dist2 = dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z;
                double cutoff = p.AdhesionCutoff * rSum;
                // Запас в 1 ppm: fl(cutoff²) может оказаться чуть меньше
                // истинного, и без запаса мы отбросили бы пару, которую точная
                // проверка `dist < cutoff` ниже ещё приняла бы.
                if (dist2 > cutoff * cutoff * 1.000001) continue;

                double dist = System.Math.Sqrt(dist2);
                if (dist < 1e-9)
                {
                    var tiny = new Vec3(0.01, 0, 0);
                    totalForce = totalForce + tiny * (p.RepulsionStrength * p.MaxForce);
                    continue;
                }
                var dirNorm = dir * (1.0 / dist);
                double overlap = rSum - dist;

                if (overlap > 0)
                {
                    double fMag = p.RepulsionStrength * overlap / rSum;
                    totalForce = totalForce + dirNorm * fMag;
                }

                double adh = p.AdhesionMatrix[cellType, allTypes[j]];
                if (adh > 0 && dist < p.AdhesionCutoff * rSum && overlap <= 0)
                {
                    double fMag = p.AdhesionStrength * adh * (1.0 - dist / (p.AdhesionCutoff * rSum));
                    totalForce = totalForce - dirNorm * fMag;
                }
            }

            foreach (var (pre, post) in edges)
            {
                int partner;
                if (pre == skipIdx) partner = post;
                else if (post == skipIdx) partner = pre;
                else continue;
                if (partner >= n) continue;

                double rSum = radius + allRadii[partner];
                double eqDist = p.ElasticEquilibrium * rSum;
                var dir = allPos[partner] - pos;
                double dist = dir.Len();
                if (dist < 1e-9) continue;
                var dirNorm = dir * (1.0 / dist);
                double strain = dist - eqDist;
                totalForce = totalForce + dirNorm * (p.ElasticStrength * strain);
            }

            double mag = totalForce.Len();
            if (mag > p.MaxForce)
                totalForce = totalForce * (p.MaxForce / mag);
            return totalForce * dt;
        }

        /// Порог параллелизации: ниже него O(n²) дешевле накладных на пул.
        public const int ParallelMinCells = 256;

        public static void StepMechanics(List<Vec3> positions, List<double> radii, List<int> types,
            List<(int, int)> edges, List<CellVelocity> velocities, MechanicsParams p, double dt) =>
            StepMechanics(positions, radii, types, edges, velocities, p, dt, parallel: false);

        public static void StepMechanics(List<Vec3> positions, List<double> radii, List<int> types,
            List<(int, int)> edges, List<CellVelocity> velocities, MechanicsParams p, double dt,
            bool parallel)
        {
            if (!p.Enabled || positions.Count == 0) return;
            int n = positions.Count;
            var forces = new Vec3[n];

            // Горячая петля — O(n²) по клеткам, поэтому работаем по МАССИВАМ:
            // индексатор List<T> на структуре не векторизуется и каждый доступ
            // платит проверку границ. Копия трёх массивов стоит O(n) и
            // окупается на первой же сотне клеток.
            var pos = new Vec3[n];
            var rad = new double[n];
            var typ = new int[n];
            for (int i = 0; i < n; i++) { pos[i] = positions[i]; rad[i] = radii[i]; typ[i] = types[i]; }

            // Пространственный хеш здесь НЕ помогает: радиус влияния
            // (AdhesionCutoff·2r = 20 при r=4) сравним с диаметром облака,
            // запрос возвращает почти всех, а сортировка кандидатов добавляет
            // свою цену. Дешёвый отсев по квадрату расстояния снимает основную
            // стоимость (sqrt) без этих накладных — проверено замером.
            //
            // forces[i] зависит только от неизменного pos, так что раздача по
            // потокам бит-в-бит совпадает с последовательным проходом.
            if (parallel && n >= ParallelMinCells)
                System.Threading.Tasks.Parallel.For(0, n,
                    i => forces[i] = PairForces(pos, rad, typ, i, p));
            else
                for (int i = 0; i < n; i++)
                    forces[i] = PairForces(pos, rad, typ, i, p);

            // Упругость синапсов — отдельным проходом по рёбрам вместо
            // O(n·E): каждое ребро вносит вклад в оба конца.
            double eq = p.ElasticEquilibrium, k = p.ElasticStrength;
            foreach (var (a, b) in edges)
            {
                if (a >= n || b >= n || a == b) continue;
                var f = ElasticForce(pos[a], pos[b], rad[a] + rad[b], eq, k);
                forces[a] = forces[a] + f;
                forces[b] = forces[b] - f;
            }

            for (int i = 0; i < n; i++)
            {
                // Клампы силы применяются после сборки всех вкладов — как в
                // референсе (там clamp стоит в конце compute_mechanics).
                double mag = forces[i].Len();
                if (mag > p.MaxForce) forces[i] = forces[i] * (p.MaxForce / mag);
                forces[i] = forces[i] * dt;

                double mass = System.Math.Max(Math2.Pow(rad[i], 3), 0.001);
                var v = velocities[i].V + forces[i] * (1.0 / mass);
                v = v * (1.0 - p.Damping);
                double vmag = v.Len();
                if (vmag > p.MaxForce) v = v * (p.MaxForce / vmag);
                velocities[i] = new CellVelocity { V = v };
                positions[i] = pos[i] + v;
            }
        }

        /// Отталкивание + адгезия одной клетки против всех остальных.
        static Vec3 PairForces(Vec3[] pos, double[] rad, int[] typ, int i, MechanicsParams p)
        {
            var pi = pos[i];
            double ri = rad[i];
            int ti = typ[i];
            double cutK = p.AdhesionCutoff, rep = p.RepulsionStrength, adhK = p.AdhesionStrength;
            var total = Vec3.Zero;
            int n = pos.Length;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                double rSum = ri + rad[j];
                double dx = pi.X - pos[j].X, dy = pi.Y - pos[j].Y, dz = pi.Z - pos[j].Z;
                double dist2 = dx * dx + dy * dy + dz * dz;
                double cutoff = cutK * rSum;
                // Пара за cutoff вклада не даёт: отталкиванию нужно
                // перекрытие (оно ближе), адгезия обнуляется ровно здесь.
                // Отсекаем по квадрату — sqrt только для участвующих пар.
                // Запас 1 ppm: fl(cutoff²) может быть чуть меньше истинного,
                // и без запаса мы отбросили бы пару, которую точная проверка
                // `dist < cutoff` ниже ещё приняла бы.
                if (dist2 > cutoff * cutoff * 1.000001) continue;

                double dist = System.Math.Sqrt(dist2);
                if (dist < 1e-9)
                {
                    total = total + new Vec3(0.01, 0, 0) * (rep * p.MaxForce);
                    continue;
                }
                double inv = 1.0 / dist;
                var dirNorm = new Vec3(dx * inv, dy * inv, dz * inv);
                double overlap = rSum - dist;

                if (overlap > 0)
                    total = total + dirNorm * (rep * overlap / rSum);

                double adh = p.AdhesionMatrix[ti, typ[j]];
                if (adh > 0 && dist < cutoff && overlap <= 0)
                    total = total - dirNorm * (adhK * adh * (1.0 - dist / cutoff));
            }
            return total;
        }

        static Vec3 ElasticForce(Vec3 a, Vec3 b, double rSum, double equilibrium, double strength)
        {
            var dir = b - a;
            double dist = dir.Len();
            if (dist < 1e-9) return Vec3.Zero;
            double strain = dist - equilibrium * rSum;
            return dir * (1.0 / dist) * (strength * strain);
        }
    }
}
