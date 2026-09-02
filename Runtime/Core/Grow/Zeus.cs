// ЗЕВС — архитектор над Прометеем. Прометей выращивает (морфогены,
// градиенты, прореживание); Зевс ПРОЖИГАЕТ АРХИТЕКТУРУ: декларирует
// проводки — монтажные схемы из блоков — и разворачивает их в Layout
// с правилами стыковки (секретируют цели, Axon от источника, Exclude
// прямого пути, геометрия).
//
// Разделение слоёв:
//   Blocks  — микросхемы (затвор, компаратор): регионы + внутренняя
//             проводка, низкоуровневые детали роста.
//   Zeus    — ПРОВОДКИ: схемы СОЕДИНЕНИЯ микросхем с параметрами-
//             портами. Архитектор думает проводками, не морфогенами.
//   Layout  — результат прожига: растёт Прометеем как обычно.
//
//   var layout = Zeus.Sensorimotor(new SensePlan { ... })
//                    .WithGate("SL", "SR", into: "ML")
//                    .Build();
//
// Библиотека первой очереди: гейт, компаратор, дифф-путь (лево/право),
// сенсорный веер, моторный сход, CPG-качалка. Каждая проводка —
// WiringDelegate: (ZeusWiring host) => void — пишет себя в сборку.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Prometheus.Runtime.Grow
{
    /// Проводка — монтаж-схема с параметрами-портами. Дополняет
    /// сборку (как BlockBuilder, но уровнем выше: может нести НЕСКОЛЬКО
    /// блоков + связи между ними + стыковки к чужим регионам).
    public delegate void WiringDelegate(ZeusWiring host);

    /// Сборка Зевса: надстройка над LayoutBuilder с доменной памятью —
    /// знает, какие порты уже заняты (детект коллизий проводок).
    public sealed class ZeusWiring
    {
        readonly LayoutBuilder _b;
        readonly HashSet<string> _usedPorts = new HashSet<string>();

        public ZeusWiring(LayoutBuilder core)
        {
            _b = core ?? throw new ArgumentNullException(nameof(core));
        }

        internal LayoutBuilder Builder => _b;

        /// Порт занят проводкой? (детект коллизий: два гейта на один
        /// выход — architectural bug, Зевс обязан ругаться, а не молчать).
        public void ClaimPort(string port, string wiring)
        {
            if (!_usedPorts.Add(port))
                throw new InvalidOperationException(
                    $"Zeus: порт '{port}' уже занят другой проводкой — " +
                    $"'{wiring}' не может его использовать");
        }

        /// Внутренние операции проксируются: проводки говорят на языке
        /// сборки (Region/Axon/Allow/Exclude/AxonTo), не трогая билдер.
        public LayoutBuilder Region(string tag) => _b.Region(tag);
        public LayoutBuilder Allow(string from, string to) => _b.Allow(from, to);
        public LayoutBuilder Exclude(string from, string to) => _b.Exclude(from, to);
        public LayoutBuilder AxonTo(string src, string morphogen, double len) =>
            _b.AxonTo(src, morphogen, len);
        public void AddBlock(BlockBuilder block) => block(_b);

        /// Собрать Layout (финал: закрыть последний регион).
        public Layout Build() => _b.Build();
    }

    /// Флюент-надстройка: ZeusWiring.And(wiring) — цепочка проводок.
    public static class ZeusExtensions
    {
        public static ZeusWiring And(this ZeusWiring host, WiringDelegate wiring)
        {
            wiring(host);
            return host;
        }
    }

    /// Библиотека проводок Зевса. Каждая —(WiringDelegate): монтажная
    /// схема из блоков с параметрами-портами. Порты заявляются ClaimPort.
    public static class Zeus
    {
        // ── ГЕЙТ-ПРОВОДКА: логическое И на пути ──────────────────────
        // «Проводка ГАМК-затвора» — та самая, с которой начался Зевс:
        // путь a∩b → into. Два совпавших входа открывают выход.
        public static WiringDelegate Gate(string a, string b, string into)
        {
            return host =>
            {
                host.ClaimPort(into + ":gate", "Gate");
                host.AddBlock(Blocks.CoincidenceGate(
                    id: PortId("G", a, b, into), a: a, b: b, output: into));
            };
        }

        // ── КОМПАРАТОР-ПРОВОДКА: прогноз vs факт ─────────────────────
        // pred + fact → ack (совпало) / err (удивление). Схема Б.
        public static WiringDelegate Compare(string pred, string fact,
            string ack, string err)
        {
            return host =>
            {
                host.ClaimPort(err + ":err", "Compare");
                host.ClaimPort(ack + ":ack", "Compare");
                host.AddBlock(Blocks.Comparator(
                    id: PortId("C", pred, fact, ack), pred: pred, fact: fact,
                    ack: ack, err: err));
            };
        }

        // ── ДИФФ-ПРОВОДКА: лево/право из симметрии ───────────────────
        // Сенсорная симметрия → моторная асимметрия (руль существа):
        // leftSense доминирует → левый мотор; rightSense → правый.
        // Кросс-инвертор: правый сенсор через I тормозит левый мотор —
        // антагонистическая пара, как у всех животных с рулём.
        public static WiringDelegate Differential(string leftSense, string rightSense,
            string leftMotor, string rightMotor)
        {
            return host =>
            {
                host.ClaimPort(leftMotor + ":diff", "Differential");
                host.ClaimPort(rightMotor + ":diff", "Differential");
                host.Allow(leftSense, leftMotor);      // прямой путь
                host.Allow(rightSense, rightMotor);
                // Кросс-торможение через интернейрон-инвертор: правый
                // сенсор гасит левый мотор. Правила стыковки: инвертор
                // СЕКРЕТИРУЕТ (приманка для роста SR), SR РАСТЁТ к нему
                // (AxonTo — порт Зевса); аксон INV→ML растёт сам
                // (ML секретирует в Core).
                string inv = "INV." + PortId("D", leftSense, rightSense, leftMotor);
                host.Region(inv)
                    .At(0, -35, 0).Radius(6).Count(8).Inhibitory()
                    .Secrete(inv)
                    .Axon(leftMotor, 130);
                host.AxonTo(rightSense, inv, 110);
                host.Allow(rightSense, inv);
                host.Allow(inv, leftMotor);
                host.Exclude(leftSense, rightMotor);   // антагонизм: без прямых кросс-путей
                host.Exclude(rightSense, leftMotor);
            };
        }

        // ── СЕНСОРНЫЙ ВЕЕР: один сенсор → несколько целей ────────────
        // Как SL рыбки: в ML, в I и в TAIL одновременно — разведение
        // потока без логики, чистая монтажная развилка.
        public static WiringDelegate Fan(string sense, params string[] targets)
        {
            return host =>
            {
                foreach (var t in targets)
                {
                    host.ClaimPort(sense + "→" + t, "Fan");
                    host.Allow(sense, t);
                }
            };
        }

        // ── МОТОРНЫЙ СХОД: несколько источников → один мотор ────────
        // Монтажная сборка: выходы N блоков/сенсоров сходятся в пул.
        public static WiringDelegate Converge(string motor, params string[] sources)
        {
            return host =>
            {
                foreach (var s in sources)
                {
                    host.ClaimPort(s + "→" + motor, "Converge");
                    host.Allow(s, motor);
                }
            };
        }

        // ── CPG-КАЧАЛКА: half-center на мотор ───────────────────────
        // Осциллятор: drive качает L/R-моторы попеременно (тяга!
        // хвост!).
        public static WiringDelegate Cpg(string drive, string leftOut, string rightOut)
        {
            return host =>
            {
                host.ClaimPort(leftOut + ":cpg", "Cpg");
                host.ClaimPort(rightOut + ":cpg", "Cpg");
                // HalfCenter целится в {id}.L_OUT/{id}.R_OUT — порты
                // качалки; их регионы объявляет ЗЕВС (блок их не строит:
                // у него только внутренность). Секрет по правилу
                // стыковки: кто аксон-цель — тот секретирует.
                string id = PortId("HC", drive, leftOut, rightOut);
                host.Region($"{id}.L_OUT").At(-30, 40, 0).Radius(6).Count(6)
                    .Secrete($"{id}.L_OUT");
                host.Region($"{id}.R_OUT").At(30, 40, 0).Radius(6).Count(6)
                    .Secrete($"{id}.R_OUT");
                host.AddBlock(Blocks.HalfCenter(id: id, drive: drive));
            };
        }

        // ── ХОЛСТ: сенсомоторная арена Зевса ─────────────────────────
        // Тот же Core, но как точка входа Зевса (для чистых сборок
        // сверху: сенсоры + моторы + затворная готовность).
        public static ZeusWiring Sensorimotor(int senseN = 20, int motorN = 20) =>
            new ZeusWiring(Blocks.Core(senseN, motorN));

        // ── Утилиты ───────────────────────────────────────────────────

        /// Идентификатор порта из тегов-участников (короткий,
        /// воспроизводимый): для авто-имён регионов проводок.
        static string PortId(string prefix, params string[] tags)
        {
            // Хэш тегов → компактный id: два одинаковых монтажа дают
            // одинаковый id (детерминизм), разные — разные.
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (var t in tags)
                {
                    h ^= (ulong)t.GetHashCode();
                    h *= 1099511628211UL;
                }
                return prefix + (h & 0xFFFF).ToString("X4");
            }
        }
    }
}
