# AGENTS.md — prometheus-unity (C#-порт prometheus-grow)

Порт Rust-ядра морфогенеза: клетки делятся, ростки ползут по градиентам
морфогенов, синапсы замыкаются распознаванием. Паритет с Rust держат
golden-трейсы — **бит-в-бит**, включая позиции сом и веса рёбер.

## Проверки

```
scripts/verify.sh            # xUnit-набор (golden-паритет с Rust)
scripts/verify.sh check      # пошаговая сверка трейса аксона с Rust
scripts/verify.sh bench      # бенчмарки диффузии/роста
```

EditMode-тесты пакета (`Tests/Editor/GoldenUnity.cs`) гоняются в
**dioma-unity** — пакет подключён туда как `file:../../prometheus-unity`
и не имеет собственного проекта. Коммитить сюда стоит перед прогоном
там.

## Что ломается, если не знать

**`Runtime/Core` чист от Unity — и должен оставаться таким.** Его asmdef
стоит с `noEngineReferences: true` и без ссылок: ядро обязано
компилироваться dotnet-ом (`Dev~/Prometheus.Runtime` собирает те же
файлы), а UnityEngine-типы (`Grid`, `Bounds`) конфликтуют с типами роста
по именам. Unity-слой живёт в `Runtime/Unity` и разрешает конфликты
using-псевдонимами — там уже случалось (`Grid` vs `UnityEngine.Grid`).

**`Dev~` невидима для Unity, и это её работа.** Все dotnet-инструменты
(xunit-тесты, bench, check, golden-probe) и их `bin/obj` живут под
тильдой; всё, что вне её, пакет тащит в редактор. Прокинуть csproj в
`Runtime/` — значит отдать Unity `Prometheus.Runtime.dll` из `bin/`,
который столкнётся с asmdef-сборкой.

**Golden-трейсы лежат в пакете, а не в git-LFS или где-то ещё:**
`Tests/Editor/Data/*.json`. Их читают ОБЕ стороны — xunit-набор
(`Dev~/Prometheus.Tests`, через `None Include` копирование) и
Unity-тесты (`PrometheusPaths.GoldenDir` через PackageInfo). Перенос
файлов должен обновить оба пути и `Dev~/golden-probe/src/main.rs`
(пишет эталоны туда при регенерации).

**`System.Text.Json` в Unity нет.** Golden-байты (бит-в-бит сравнение
через `ulong.Parse`) — забота xunit-набора. Unity-сторона проверяет
то, что умеет только редактор: Burst против CPU, поведение без шейдера,
и главный инвариант игрового пути — нарезка `RunSlice(k)` даёт тот же
коннектом, что `Grow` одним куском, при любом k. Если меняешь
`GrowHandle`/`World.Step` — этот тест первый, кто это поймает.

**Burst-джоба диффузии писала НИЧЕГО из-за safety-ограничения.**
`[WriteOnly]`-массив в `IJobParallelFor` разрешает запись только в
элемент с индексом job-index; наш `idx` — декодированный, и safety
роняла джобу `IndexOutOfRange` (не сразу заметно: границы сетки
копируются из входа, и тест сравнивал нули с нулями бы, если бы не
центральная ячейка). Атрибут `NativeDisableParallelForRestriction` на
`FieldOut` обязателен. Тест `BurstBackend_Matches_Cpu` держит.

**FloatPrecision в Burst не имеет `Strict`** (только `High`), а паритет
на double всё равно держит `FloatMode.Strict`. Указать несуществующий
уровень — CS0117 при первом реальном импорте, а до него dotnet-сборка
проходит: `Unity.Burst` в netstandard-цели недоступен.

**Формат `ToJson`/`ToDiomaJson` — контракт с эталонами.** Числа
пишутся кратчайшим round-trip представлением; Mono и Core расформатировывают
«R» по-разному (9 и 8 значащих цифр), поэтому сравнивать мост с эталоном
надо ЧЕРЕЗ МОЗГ (`DiomaBrain.FromJson` + `CsrValues()`), а не строкой —
иначе тест ловит форматировщики, а не паритет.
