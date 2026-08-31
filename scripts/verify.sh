#!/usr/bin/env bash
# Самопроверка пакета com.dioma.prometheus без Unity-редактора.
#   scripts/verify.sh            → xunit-набор (golden-паритет с Rust)
#   scripts/verify.sh check      → пошаговая сверка трассы роста axon0
#   scripts/verify.sh bench      → бенчмарки диффузии и роста
# Ядро собирается прямо из исходников пакета (Dev~/Prometheus.Runtime
# компилирует Runtime/Core/**), поэтому проверяется тот же код, который
# увидит редактор. Unity-слой (Runtime/Unity) требует UnityEngine — его
# паритет ловят EditMode-тесты в dioma-unity.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# dotnet на этой машине лежит вне PATH — ищем по обоим адресам.
find_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return
  fi
  local candidate="${USERPROFILE:-$HOME}/.dotnet/dotnet.exe"
  candidate="${candidate//\\//}"
  if [[ -x "$candidate" ]]; then
    printf '%s\n' "$candidate"
    return
  fi
  echo "dotnet SDK не найден: нет в PATH и нет $candidate" >&2
  echo "поставь .NET 8 SDK или добавь его в PATH" >&2
  exit 127
}

DOTNET="$(find_dotnet)"
MODE="${1:-tests}"

case "$MODE" in
  tests)
    echo "── xunit-набор ($("$DOTNET" --version)) ──"
    "$DOTNET" test 'Dev~/Prometheus.Tests/Prometheus.Tests.csproj' -c Release --nologo -v q
    ;;
  check)
    "$DOTNET" run -c Debug --project 'Dev~/check/check.csproj'
    ;;
  bench)
    "$DOTNET" run -c Release --project 'Dev~/bench/bench.csproj' -- "${@:2}"
    ;;
  *)
    echo "неизвестный режим '$MODE': tests | check | bench" >&2
    exit 2
    ;;
esac
