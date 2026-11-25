# Темы Tandem

Тема — JSON-файл с цветами, шрифтами и размерами, которыми рисуется весь интерфейс. Формат — `tokens.json`
дизайн-системы Tandem; встроенная тема «Stone» (`src/Tandem.Core/Themes/stone.json`) — её образец.

## Где лежат темы

Папка `Themes` рядом с `settings.json`:

| Система | Папка |
|---|---|
| Windows | `%APPDATA%\Tandem\Themes` |
| macOS | `~/Library/Application Support/Tandem/Themes` |

Каждый файл `*.json` — отдельная тема. Идентификатор темы — имя файла без `.json`; в списке она показана под
именем из поля `name` (нет поля — под идентификатором). Папку читают заново при каждом обращении, поэтому новая
тема появляется без перезапуска. Файл `stone.json` в этой папке заменяет встроенную тему. Файл, который не удалось
прочитать, остаётся в списке с текстом ошибки, а применяется встроенная тема.

## Что берётся из встроенной темы

В файле нужны только те токены, которые вы меняете. Всё, чего в нём нет (цвет, стиль текста, семейство шрифтов,
размер), берётся из встроенной темы «Stone». Любой раздел можно опустить целиком.

Размер шрифта и высота строки **списка файлов** остаются пользовательскими настройками и темой не задаются.

## Формат файла

```json
{
  "name": "Моя тема",
  "color":   { "tokens": [ { "name": "surface", "value": { "light": "#bbb8b0", "dark": "#2a2927" } } ] },
  "type":    { "families": { "ui": "...", "mono": "..." },
               "groups": [ { "family": "ui", "styles": [ { "name": "item", "fontSize": "14px",
                                                           "lineHeight": "22px", "fontWeight": 600 } ] } ] },
  "spacing": { "tokens": [ { "name": "space-2", "value": "8px" } ] },
  "size":    { "tokens": [ { "name": "row-height", "value": "22px" } ] },
  "radius":  { "tokens": [ { "name": "radius-button", "value": "3px" } ] }
}
```

- Допустимы комментарии `//` и `/* */` и запятая после последнего элемента.
- Прочие поля (`usage`, `sample`, `shadow`, `version`…) игнорируются.
- Ошибка (не JSON, корень не объект, токен без `name`, неверный цвет, нечисловой размер) делает файл непригодным.

### Цвета (`color`)

Значение — `{ "light": "#rrggbb", "dark": "#rrggbb" }` для светлой и тёмной темы приложения. Одна строка
(`"value": "#ffffff"`) или только один из вариантов применяется к обоим. Допустимы `#rgb`, `#rrggbb`, `#aarrggbb`.

| Токен | Чем красится |
|---|---|
| `surface` | Фон списков файлов, окон и диалогов |
| `surface-raised` | Строка заголовка окна, строка вкладок панели, фон текста в просмотрщике, список сочетаний |
| `bar` | Полосы панели (диски, путь неактивной панели, строка состояния), F-клавиши, кнопки, панель просмотрщика |
| `bar-hover` | Кнопка и F-клавиша под указателем мыши |
| `bar-pressed` | Кнопка и F-клавиша в момент нажатия |
| `field` | Фон полей ввода, выпадающих списков, флажков, командной строки |
| `line` | Разделитель между панелями, рамки полей без фокуса, ползунок прокрутки |
| `edge` | Рамки элементов управления и неактивная рамка курсора (контраст 3:1 и выше) |
| `ink` | Основной текст |
| `ink-muted` | Второстепенный текст: расширения, «свободно … ГБ», сочетания клавиш, подсказки |
| `ink-disabled` | Текст недоступной кнопки |
| `accent` | Строка пути активной панели и значок приложения; показывает, какая панель активна |
| `on-accent` | Текст и стрелки на `accent` |
| `cursor` | Строка под курсором в активной панели |
| `focus` | Рамка поля в фокусе, подчёркивание выбранной вкладки, фон выделенного текста в поле |
| `on-focus` | Выделенный текст на `focus` |
| `folder` | Значок папки и «..» в списке |
| `file` | Значок файла в списке |
| `syntax` | Скобки и ключи JSON в просмотрщике и редакторе |

### Шрифты и стили текста (`type`)

`families` — списки семейств в стиле CSS для токенов `ui` (интерфейс) и `mono` (код). Стиль текста:
`name`, `fontSize`, `lineHeight` (пиксели), `fontWeight` (100–900, по умолчанию 400) и `family`
(`ui` или `mono`; если не задано — берётся `family` группы, затем `ui`).

| Стиль | Где используется |
|---|---|
| `dialog-tab` | Вкладки окна «Настройки», подзаголовок раздела |
| `dialog-text` | Текст малых диалогов, подписи флажков, панель просмотрщика |
| `item` | Имена файлов и папок, пункты меню, названия команд, текст кнопок |
| `data` | Расширение, размер, дата, путь, строка состояния, сочетания клавиш |
| `caption` | Строка заголовка окна и диалога |
| `code` | Текст в просмотрщике и редакторе, номера строк (семейство `mono`) |

### Размеры, отступы, скругления (`spacing`, `size`, `radius`)

Значение — число или строка с `px` (`"22px"`, `22`); все токены этих трёх разделов — размеры в пикселях.

| Токен | Что задаёт |
|---|---|
| `space-1` (4), `space-2` (8), `space-3` (12), `space-4` (16), `space-6` (24), `space-7` (28) | Отступы и промежутки |
| `row-height` (22) | Строка списка и полосы панели |
| `fkey-height` (26) | Строка F-клавиш |
| `control-height` (29) | Кнопка |
| `field-height` (32) | Поле ввода, выпадающий список, поиск |
| `shortcut-row-height` (38) | Строка списка сочетаний в «Настройках» |
| `pane-gap` (4) | Ширина разделителя между панелями |
| `radius-field` (2), `radius-button` (3), `radius-control` (4), `radius-window` (8) | Скругления |

В скобках — значения встроенной темы.

## Минимальный пример

Тема, которая меняет только цвет акцента и высоту строки; остальное берётся из «Stone»:

```json
{
  "name": "Stone, высокие строки",
  "color": { "tokens": [ { "name": "accent", "value": { "light": "#25555c", "dark": "#357c86" } } ] },
  "size":  { "tokens": [ { "name": "row-height", "value": "26px" } ] }
}
```

Готовый файл с такой структурой — [`themes/example-minimal.json`](themes/example-minimal.json): скопируйте его в
папку `Themes` и меняйте значения.

## Поставляемые темы (по мотивам Notepad++)

С приложением идут шесть тем по мотивам тем Notepad++. Их файлы встроены в сборку `Tandem.Core`
(`src/Tandem.Core/Themes/Bundled/*.json`). При каждом запуске приложение копирует в папку `Themes` те из них,
**которых там ещё нет**. Файл с таким же именем не перезаписывается, поэтому изменения пользователя сохраняются.
Из этого следуют два правила:

- удалённая поставляемая тема вернётся при следующем запуске; чтобы убрать её из списка насовсем, оставьте файл и
  правьте его;
- новая версия поставляемой темы не заменит уже скопированную копию; чтобы получить новую версию, удалите файл
  перед запуском.

| Файл | Название в списке | Исходный XML Notepad++ | Вид | Автор темы Notepad++ | Лицензия |
|---|---|---|---|---|---|
| `npp-obsidian.json` | Obsidian (Notepad++) | `Obsidian.xml` | тёмная | Joni Eskelinen | в файле не указана |
| `npp-monokai.json` | Monokai (Notepad++) | `Monokai.xml` | тёмная | © 2008 Fabio Zendhi Nagao | MIT |
| `npp-twilight.json` | Twilight (Notepad++) | `Twilight.xml` | тёмная | © 2008 Fabio Zendhi Nagao, 2011–2014 Renato Silva | MIT |
| `npp-bespin.json` | Bespin (Notepad++) | `Bespin.xml` | тёмная | © 2009 Oren Farhi, Orizen Designs | MIT |
| `npp-solarized.json` | Solarized (Notepad++) | `Solarized-light.xml` + `Solarized.xml` | светлая и тёмная | Paul Neubauer, по Solarized Ethan Schoonover | CC BY 3.0 |
| `npp-navajo.json` | Navajo (Notepad++) | `Navajo.xml` | светлая | Paul Neubauer, по navajo.vim R. Edward Ralston | CC BY 3.0 |

Исходные файлы лежат в репозитории notepad-plus-plus, в папке `PowerEditor/installer/themes`. Тексты лицензий MIT
приведены в заголовках этих XML.

**Светлый и тёмный вариант.** Тема Notepad++ рассчитана на один фон. Поэтому каждая поставляемая тема задаёт
**все** цветовые токены одним значением для обоих вариантов. Так тема выглядит одинаково при любом выборе
«светлая / тёмная / как в системе». Если бы какой-то токен был пропущен, он взялся бы из Stone **того варианта,
который выбран сейчас**: тогда при светлом режиме на тёмном фоне Monokai появились бы светлые кнопки. Solarized —
настоящая пара, поэтому в его файле `light` взят из `Solarized-light.xml`, а `dark` — из `Solarized.xml`.

**Происхождение цветов.** Каждый цвет взят из XML темы без изменений. Поле `usage` у токена называет стиль
Notepad++, из которого он взят, например `Monokai.xml «Line number margin bg»`. Поле `origin` в корне файла
указывает источник, автора и лицензию. Оба поля движок тем пропускает. Шрифты и размеры берутся из Stone.

Как стили Notepad++ сопоставлены токенам. Генератор перебирает кандидатов по порядку и берёт первого, у которого
достаточный контраст:

| Токен | Стиль Notepad++ (кандидаты по порядку) |
|---|---|
| `surface`, `field` | Default Style, фон |
| `ink` | Default Style, текст → C++ OPERATOR → Caret colour (первый с контрастом 4.5:1 к фону и полям) |
| `surface-raised`, `bar` | Line number margin, фон → Fold margin → Current line background |
| `bar-hover` | Current line background → Fold margin → Selected text colour, фон |
| `bar-pressed` | Selected text colour, фон → Fold margin → Current line background |
| `cursor` | Selected text colour, фон → Current line background |
| `line` | Indent guideline style → Edge colour |
| `edge` | Line number margin, текст → Indent guideline → C++ COMMENT → Edge colour → C++ OPERATOR |
| `ink-muted` | Line number margin, текст → C++ COMMENT → C++ OPERATOR → Default Style, текст |
| `ink-disabled` | C++ COMMENT → Indent guideline → Line number margin, текст |
| `accent` | C++ INSTRUCTION WORD → TYPE WORD → STRING → NUMBER → собственный Active tab focused indicator → PREPROCESSOR |
| `focus` | Brace highlight style → Caret colour → C++ STRING → NUMBER → INSTRUCTION WORD |
| `on-accent`, `on-focus` | Default Style, фон или текст: что контрастнее |
| `folder` | C++ TYPE WORD → NUMBER → STRING → INSTRUCTION WORD → PREPROCESSOR |
| `file` | C++ COMMENT → Line number margin, текст → Indent guideline → C++ OPERATOR |
| `syntax` | C++ NUMBER → STRING → OPERATOR → TYPE WORD |

Отдельных токенов для ошибок и отмеченных файлов в движке тем нет. Поэтому стили Notepad++ «Bad brace colour»
и «Find Mark Style» не используются.

**Контраст.** У тёмных тем и Navajo все основные пары проходят: текст 4.5:1, рамки и значки 3:1. Палитра
Solarized сама по себе мягкая, и в ней есть провалы:

| Пара | Solarized light | Solarized dark |
|---|---|---|
| `ink-muted` / `bar` | 4.39 | 4.11 |
| `on-accent` / `accent` | 4.29 | — |
| `syntax` / `surface-raised` | 4.39 | — |

В светлом Solarized всего два фона (`#fdf6e3` и `#eee8d5`). Поэтому полосы, строка курсора и кнопка под указателем
там одного цвета, а `folder` совпадает с `accent`.

Генератор: [`tools/themes/npp_to_tandem.py`](../tools/themes/npp_to_tandem.py). Он читает XML Notepad++ и пишет JSON и отчёт о контрасте.
