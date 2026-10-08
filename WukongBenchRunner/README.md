# WukongBenchRunner

C#-инструмент для автоматизированного запуска двух проходов **Black Myth: Wukong Benchmark Tool**:

- CPU-тест: низкое разрешение и минимальные графические настройки, чтобы снизить влияние GPU.
- GPU-тест: высокие настройки, чтобы максимально нагрузить видеокарту.

После запусков программа формирует Markdown-отчёт с характеристиками компьютера, результатами benchmark и использованными настройками.

## Требования

- Windows.
- Steam.
- Установленный **Black Myth: Wukong Benchmark Tool**.
- .NET 8 SDK.

## Запуск

Из папки, где лежит проект:

```powershell
dotnet run --project .\WukongBenchRunner.csproj -- --out report.md
```

Если запускаете из родительской папки:

```powershell
dotnet run --project .\WukongBenchRunner -- --out report.md
```

Если автообнаружение не сработало, можно указать пути вручную:

```powershell
dotnet run --project .\WukongBenchRunner.csproj -- `
  --game-dir "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool" `
  --exe "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Binaries\Win64\b1-Win64-Shipping.exe" `
  --config "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Saved\Config\Windows\GameUserSettings.ini" `
  --out report.md
```

Запуск через Steam:

```powershell
dotnet run --project .\WukongBenchRunner.csproj -- --use-steam --out report.md
```

Проверка записи настроек без запуска benchmark:

```powershell
dotnet run --project .\WukongBenchRunner.csproj -- --dry-run
```

## Как работает

1. Программа ищет установленный Benchmark Tool в Steam-библиотеках.
2. Находит или создаёт `GameUserSettings.ini`.
3. Создаёт резервную копию конфигурации.
4. Применяет настройки CPU-теста и запускает benchmark.
5. После завершения ищет свежие файлы логов/результатов.
6. Повторяет процесс для GPU-теста.
7. Создаёт отчёт `report.md`.

## Объяснение выбора настроек

Для CPU-теста используются низкое разрешение, низкий масштаб рендера и минимальные графические настройки. Это уменьшает нагрузку на видеокарту, поэтому результат сильнее зависит от процессора.

Для GPU-теста используются высокие настройки качества, высокий масштаб рендера и отключённый лимит FPS. Это увеличивает нагрузку на рендеринг, освещение, тени, текстуры и постобработку, поэтому результат сильнее зависит от видеокарты.

## Ограничение

У Benchmark Tool нет стабильно документированного публичного CLI для полного автоматического старта теста и экспорта результата. Поэтому программа запускает приложение, ждёт завершения процесса и пытается автоматически найти свежие файлы результатов/логов.

Если в вашей версии benchmark нужно нажать кнопку запуска вручную, нажмите её в открывшемся окне. После закрытия benchmark программа продолжит сбор отчёта.
