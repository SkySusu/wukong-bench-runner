# WukongBenchRunner

Инструмент на C# для локального автоматизированного запуска двух проходов **Black Myth: Wukong Benchmark Tool** из Steam:

- CPU-тест: низкое разрешение и минимальные графические настройки, чтобы снизить влияние видеокарты.
- GPU-тест: высокие/кинематографические настройки и высокий масштаб рендера, чтобы максимально нагрузить видеокарту.

После каждого прохода программа собирает характеристики компьютера, ищет свежие файлы результатов/логов и формирует удобный Markdown-отчёт.

## Требования

- Windows.
- Установленный Steam.
- Установленный **Black Myth: Wukong Benchmark Tool**.
- .NET 8 SDK.

Steam AppID benchmark tool: `3132990`.

## Запуск

Из корня проекта:

```powershell
dotnet run --project .\WukongBenchRunner -- --out report.md
```

Если автообнаружение не сработало, укажите пути вручную:

```powershell
dotnet run --project .\WukongBenchRunner -- `
  --game-dir "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool" `
  --exe "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Binaries\Win64\b1-Win64-Shipping.exe" `
  --config "C:\Program Files (x86)\Steam\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Saved\Config\Windows\GameUserSettings.ini" `
  --out report.md
```

Проверить запись настроек без запуска benchmark:

```powershell
dotnet run --project .\WukongBenchRunner -- --dry-run
```

Если прямой запуск `.exe` не подходит, можно запустить через Steam:

```powershell
dotnet run --project .\WukongBenchRunner -- --use-steam --out report.md
```

## Что делает программа

1. Находит папку Steam-библиотеки и каталог `Black Myth Wukong Benchmark Tool`.
2. Находит `GameUserSettings.ini`.
3. Создаёт резервную копию конфигурации перед каждым проходом.
4. Записывает настройки CPU-прохода.
5. Запускает benchmark и ждёт завершения процесса.
6. Ищет свежие файлы логов/результатов и парсит FPS-метрики.
7. Повторяет те же шаги для GPU-прохода.
8. Создаёт Markdown-отчёт с характеристиками ПК, результатами и использованными настройками.

## Выбор настроек

### CPU-тест

Для CPU-теста выбраны:

- `1280x720`
- `sg.ResolutionQuality = 50`
- все основные `sg.*Quality = 0`
- VSync выключен
- лимит FPS выключен
- ray tracing выключен

Такой набор уменьшает пиксельную и графическую нагрузку, поэтому итоговый FPS сильнее зависит от процессора, подготовки кадров и общей логики движка.

### GPU-тест

Для GPU-теста выбраны:

- `1920x1080`
- `sg.ResolutionQuality = 100`
- все основные `sg.*Quality = 3`
- VSync выключен
- лимит FPS выключен
- ray tracing включён, если benchmark поддерживает эти ключи

Такой набор увеличивает нагрузку на рендеринг, тени, освещение, отражения, текстуры и постобработку, поэтому результат сильнее зависит от видеокарты.

## Важное ограничение

У Benchmark Tool нет стабильно документированного публичного CLI для прямого старта теста и экспорта результата. Поэтому программа запускает найденный `.exe`, ждёт его завершения и анализирует свежие файлы рядом с игрой и в `%LOCALAPPDATA%\b1`.

Если на вашей версии benchmark после запуска нужно нажать кнопку `Start Benchmark`, это ограничение самого приложения. В таком случае можно:

- один раз настроить запуск через параметры `--launch-args`, если ваша версия их поддерживает;
- либо оставить окно benchmark открытым, нажать запуск вручную, а сбор отчёта программа выполнит после закрытия процесса.
