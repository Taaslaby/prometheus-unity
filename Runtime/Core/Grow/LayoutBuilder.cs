// Fluent-билдер Layout: схема мозга читается как рецепт, без конструкторных
// списков и массивов координат.
//
//   var layout = LayoutBuilder.Create()
//       .Region("SL").At(-60, -60, 0).Radius(15).Count(20).Axon("ML", 170)
//       .Region("I").At(0, -30, 0).Radius(12).Count(16).Inhibitory().Axon("ML", 140)
//       .Region("ML").At(-60, 60, 0).Radius(15).Count(20).Secrete("ML")
//       .Allow("SL", "ML").Allow("I", "ML")
//       .Build();

using System.Collections.Generic;

namespace Prometheus.Runtime.Grow
{
    public class LayoutBuilder
    {
        readonly List<RegionSpec> _regions = new List<RegionSpec>();
        readonly List<(string, string)> _pairs = new List<(string, string)>();
        RegionSpec _current;

        public static LayoutBuilder Create() => new LayoutBuilder();

        /// Закрыть предыдущий регион (если был) и начать новый с тегом.
        public LayoutBuilder Region(string tag)
        {
            CloseRegion();
            _current = new RegionSpec { Tag = tag };
            return this;
        }

        /// Центр региона (мировые координаты).
        public LayoutBuilder At(double x, double y, double z)
        {
            _current.Center = new[] { x, y, z };
            return this;
        }

        /// Радиус облака сом.
        public LayoutBuilder Radius(double r) { _current.Radius = r; return this; }

        /// Число нейронов.
        public LayoutBuilder Count(int n) { _current.Count = n; return this; }

        /// Тормозной регион (nt = −1).
        public LayoutBuilder Inhibitory() { _current.Inhibitory = true; return this; }

        /// Секретируемый морфоген (он же цель-приманка для чужих аксонов).
        public LayoutBuilder Secrete(string morphogen) { _current.Secrete = morphogen; return this; }

        /// Аксон: растём к морфогену `target`, максимум `maxLen` мкм.
        public LayoutBuilder Axon(string target, double maxLen)
        {
            _current.Axons.Add(AxonSpec.To(target, maxLen));
            return this;
        }

        /// Разрешённая пара синапсов (tag → tag).
        public LayoutBuilder Allow(string from, string to)
        {
            _pairs.Add((from, to));
            return this;
        }

        /// Разрешить связку «от A ко всем из списка B».
        public LayoutBuilder Allow(string from, params string[] tos)
        {
            foreach (var to in tos) _pairs.Add((from, to));
            return this;
        }

        /// Самосинапсы внутри региона (A → A).
        public LayoutBuilder AllowSelf(string tag)
        {
            _pairs.Add((tag, tag));
            return this;
        }

        /// Готовая схема (закрывает последний регион).
        public Layout Build()
        {
            CloseRegion();
            var l = new Layout();
            l.Regions.AddRange(_regions);
            l.AllowedPairs.AddRange(_pairs);
            return l;
        }

        void CloseRegion()
        {
            if (_current == null) return;
            if (_current.Center == null)
                _current.Center = new[] { 0.0, 0.0, 0.0 };
            _regions.Add(_current);
            _current = null;
        }
    }
}
