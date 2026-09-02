// Прометей-Билдер: библиотека блоков-мотивов. Схемы не пишутся по
// нейрону — собираются из микросхем: каждая — проверенная конфигурация
// с внутренней проводкой, growing-морфогенезом и портами для стыковки.
//
// Зачем: LayoutBuilder описывает РЕГИОНЫ; повторяющиеся схемы
// (рефлекс, затвор, компаратор) каждый раз пересобираются руками и
// расходятся в деталях. Блок = схема-функция: внутренние Allow уже
// проставлены, имена портов оговорены — сборка мозга становится
// композицией блоков, как печатные платы из микросхем.
//
//   var layout = Blocks.Core()
//       .And(Blocks.CoincidenceGate("G1", a: "SL", b: "SR", out: "ML"))
//       .Build();
//
// Порт — регион-край блока: тег с суффиксом блока. Стыковка внешним
// Allow от/к порту. Морфоген-приманка: каждый блок секретирует
// уникальный морфоген (по имени блока), чтобы аксоны хозяина могли
// целиться в блок как в единое целое.

using System.Collections.Generic;

namespace Prometheus.Runtime.Grow
{
    /// Библиотека мотивов. Каждый метод возвращает билдер-делегат:
    /// (LayoutBuilder) => LayoutBuilder — блок дописывает СЕБЯ в чужую
    /// сборку (регионы + внутренние Allow), не зная соседей.
    public static class Blocks
    {
        // ── Коинцидентный затвор (ГАМК-гейт) ─────────────────────────
        //
        // Два входа a и b. Тонический ГАМК-интернейрон держит выход
        // OUT закрытым. Каждый вход ОДИН не открывает (тормоз сильнее);
        // ОДНОВРЕМЕННЫЕ a и b (окно tau_syn) вместе снимают тормоз —
        // сигнал проходит. Логическое И спайковым образом: дизингибция.
        //
        //   a ─►┐
        //       ├─► [a∩b: disin] ──тормозит──► [gate: ГАМК] ──► OUT
        //   b ─►┘
        //
        // Порты: входы a, b (через Allow("SL", "G1.A") и т.п.),
        // выход OUT. Гейты-нейроны: A/B — маленькие реле-интеграторы
        // (сходятся в disin), disin — тормозной к gate, gate —
        // тормозной к OUT-соседям.
        // Правило проводки Прометея: Axon растёт К МОРФОГЕНУ, а морфоген
        // секретирует ЦЕЛЬ. Значит каждая внутренняя цель блока
        // секретирует свой тег — иначе аксон не найдёт приманку и
        // вход в блок повиснет (замер теста: 'A обязан достать DIS'
        // падал ровно на это).
        public static BlockBuilder CoincidenceGate(string id, string a, string b,
            string output)
        {
            return host => host
                
                // Входные реле СЕКРЕТИРУЮТ (приманка): источник-хозяин
                // растёт к ним аксионом (HostWire на стороне сборки).
                // Первая версия росла сама (Axon(a)) — направление
                // перевёрнуто: аксон А тянулся К морфогену SL и давал
                // рёбра A→SL, а вход — это SL→A (замер: 0 рёбер).
                // Реле прижаты к источникам: морфогены Core (ML/MR)
                // заглушают далёкую приманку — SR→G1.B не дорастал
                // (замер: SL→G1.A=3, SR→G1.B=0 на равных дистанциях —
                // прореживание съедало одиночные рёбра). Больше реле,
                // ближе к источникам, длиннее аксон.
                .Region($"{id}.A").At(-45, -45, 0).Radius(8).Count(8)
                    .Secrete($"{id}.A")
                    .Axon($"{id}.DIS", 60).Axon(output, 200)
                .Region($"{id}.B").At(45, -45, 0).Radius(8).Count(8)
                    .Secrete($"{id}.B")
                    .Axon($"{id}.DIS", 60).Axon(output, 200)
                .Region($"{id}.DIS").At(0, -5, 0).Radius(5).Count(8).Inhibitory()
                    .Secrete($"{id}.DIS")
                    .Axon($"{id}.GATE", 40)
                .Region($"{id}.GATE").At(0, 10, 0).Radius(5).Count(6).Inhibitory()
                    .Secrete($"{id}.GATE")
                    .Axon(output, 80)
                // Входные порты: источник входит в реле блока — без
                // пары блок глух (замер: SL→G1.A = 0 рёбер).
                .Allow(a, $"{id}.A")
                .Allow(b, $"{id}.B")
                .Allow($"{id}.A", $"{id}.DIS")
                .Allow($"{id}.B", $"{id}.DIS")
                // Носитель сигнала: реле несут ВОЗБУЖДЕНИЕ на выход
                // (дизингибция открывает путь, но не даёт плюс — вход
                // обязан проходить сам). GATE-тормоз давит его, пока
                // входы не совпали.
                .Allow($"{id}.A", output)
                .Allow($"{id}.B", output)
                .Allow($"{id}.DIS", $"{id}.GATE")
                .Allow($"{id}.GATE", output)
                // ГЕЙТ ПОДМЕНЯЕТ прямой путь: иначе вход→выход пробивает
                // мотор мимо затвора (замер: SL→ML прямо 56 рёбер —
                // одиночный SL открывал мотор без второго входа).
                .Exclude(a, output)
                .Exclude(b, output);
        }

        // ── Рефлекторная дуга (S→I→M) ────────────────────────────────
        // Классика dioma: сенсор через тормозной интернейрон на мотор.
        // Порты: вход s, выход m.
        public static BlockBuilder ReflexArc(string id, string s, string m)
        {
            return host => host
                .Region($"{id}.I").At(0, -15, 0).Radius(8).Count(10).Inhibitory()
                    .Axon(m, 120)
                .Allow(s, $"{id}.I")
                .Allow($"{id}.I", m);
        }

        // ── Полукольцо (half-center осциллятор) ─────────────────────
        // Два тормозных, взаимно подавляющих: основа CPG. Снаружи
        // подключается вечный тонус — рождение ритма при двойном
        // торможении. Порты: тонус in, выходы L, R.
        public static BlockBuilder HalfCenter(string id, string drive)
        {
            return host => host
                .Region($"{id}.L").At(-15, 0, 0).Radius(8).Count(10).Inhibitory()
                    .Secrete($"{id}.L")
                    .Axon($"{id}.R", 40).Axon($"{id}.L_OUT", 60)
                .Region($"{id}.R").At(15, 0, 0).Radius(8).Count(10).Inhibitory()
                    .Secrete($"{id}.R")
                    .Axon($"{id}.L", 40).Axon($"{id}.R_OUT", 60)
                .Allow(drive, $"{id}.L")
                .Allow(drive, $"{id}.R")
                .Allow($"{id}.L", $"{id}.R")
                .Allow($"{id}.R", $"{id}.L")
                .Allow($"{id}.L", $"{id}.L_OUT")
                .Allow($"{id}.R", $"{id}.R_OUT");
        }

        // ── Компаратор предсказания (surprise) ──────────────────────
        // Прогноз (внешний регион PRED, из HAAP-Memory) и факт (сенсор
        // SENSE) сходятся на затворе: совпадение открывает путь ACK,
        // несовпадение — путь ERR остаётся тонически открытым.
        // Порты: pred, fact → ack, err.
        // (Схема Б из разговора: удивление = незакрытый затвор.)
        public static BlockBuilder Comparator(string id, string pred, string fact,
            string ack, string err)
        {
            return host => host
                .Region($"{id}.A").At(-20, 0, 0).Radius(5).Count(4)
                    .Axon($"{id}.DIS", 50)
                .Region($"{id}.F").At(20, 0, 0).Radius(5).Count(4)
                    .Axon($"{id}.DIS", 50)
                .Region($"{id}.DIS").At(0, 8, 0).Radius(5).Count(6).Inhibitory()
                    .Secrete($"{id}.DIS")
                    .Axon($"{id}.GATE", 40).Axon(ack, 90)
                .Region($"{id}.GATE").At(0, 20, 0).Radius(5).Count(4).Inhibitory()
                    .Secrete($"{id}.GATE")
                    .Axon(err, 80)
                .Allow(pred, $"{id}.A")
                .Allow(fact, $"{id}.F")
                .Allow($"{id}.A", $"{id}.DIS")
                .Allow($"{id}.F", $"{id}.DIS")
                .Allow($"{id}.DIS", $"{id}.GATE")
                .Allow($"{id}.GATE", err)
                // Путь подтверждения: совпало → затвор закрыл ERR,
                // дизингибирующий сигнал открыл ACK. Рост по аксонам:
                // Axon(ack) — иначе Allow один давал бы разрешение
                // без приманки (замер теста: DIS→ACK не рос).
                .Allow($"{id}.DIS", ack)
                // Затвор подменяет прямой путь факта к ERR (аналогично
                // CoincidenceGate): ERR гасится ТОЛЬКО через C1.GATE.
                .Exclude(fact, err);
        }

        // ── Готовый мозг-сборка: ядро сенсоров/моторов рыбки ─────────
        // Стартовый холст: SL/SR/I/ML/MR/TAIL как в Layouts.Fish, но
        // собранный из блоков — точка входа для экспериментов со
        // схемами на живом виде.
        // Правило стыковки портов: региона-ЦЕЛЬ секретирует свой тег —
        // иначе выходной аксон блока (Axon("ML")) не найдёт приманку
        // (как в Layouts.Fish: там ML/MR/TAIL секретируют). Входам блоков
        // секрет не нужен: СЕНСОРЫ никуда не растут, их целит хозяин.
        public static LayoutBuilder Core(int senseN = 20, int motorN = 20)
        {
            return LayoutBuilder.Create()
                .Region("SL").At(-60, -60, 0).Radius(15).Count(senseN)
                    .Axon("ML", 170).Axon("TAIL", 200)
                .Region("SR").At(60, -60, 0).Radius(15).Count(senseN)
                    .Axon("MR", 170).Axon("TAIL", 200)
                .Region("I").At(0, -30, 0).Radius(12).Count(16).Inhibitory()
                    .Axon("ML", 140).Axon("MR", 140).Axon("TAIL", 160)
                .Region("ML").At(-60, 60, 0).Radius(15).Count(motorN)
                    .Secrete("ML")
                .Region("MR").At(60, 60, 0).Radius(15).Count(motorN)
                    .Secrete("MR")
                .Region("TAIL").At(0, 60, 0).Radius(16).Count(24)
                    .Secrete("TAIL")
                .Allow("SL", "ML").Allow("SL", "I").Allow("SL", "TAIL")
                .Allow("SR", "MR").Allow("SR", "I").Allow("SR", "TAIL")
                .Allow("I", "ML").Allow("I", "MR").Allow("I", "TAIL");
        }
    }

    /// Делегат блока: дописывает себя в сборку.
    public delegate LayoutBuilder BlockBuilder(LayoutBuilder host);

    public static class BlockExtensions
    {
        /// Встроить блок в сборку (флюентно: блок возвращает билдер).
        public static LayoutBuilder And(this LayoutBuilder host, BlockBuilder block) =>
            block(host);

        /// HostWire: аксон региона-источника к приманке блока — входной
        /// порт. Блок секретирует реле, хозяин РАСТЁТ к нему: направление
        /// по правилу Прометея (растёт владелец аксона).
        public static LayoutBuilder Wire(this LayoutBuilder host, string sourceTag,
            string targetMorphogen, double maxLen = 120)
        {
            var region = host.GetType()
                .GetField("_current", System.Reflection.BindingFlags.NonPublic |
                                     System.Reflection.BindingFlags.Instance);
            // Проще публичный путь: Region(tag) с уже закрытым прошлым —
            // вместо рефлексии расширяем builder публичным методом.
            return host.AxonTo(sourceTag, targetMorphogen, maxLen);
        }

        /// Встроить блок и связать порт с внешним регионом.
        public static LayoutBuilder And(this LayoutBuilder host, BlockBuilder block,
            string from, string to)
        {
            block(host);
            return host.Allow(from, to);
        }
    }
}
