# LayoutGuard
LayoutGuard is a lightweight portable tray utility that fixes the common keyboard layout switching bug in Windows 10/11. It monitors your hotkey (e.g. Shift+Alt) and automatically switches the layout if Windows fails to do so. Left-click the tray icon to toggle manually. Settings are saved in LayoutGuard.ini.


LayoutGuard - портативная утилита в трее, которая исправляет частый баг Windows 10/11 с переключением раскладки клавиатуры.

Отслеживает заданное сочетание (например, Shift+Alt) и, если система не переключила раскладку, автоматически делает это. ЛКМ по иконке - ручное переключение. Настройки в файле LayoutGuard.ini.

Простое и надёжное решение проблемы, когда Alt+Shift срабатывает не с первого раза или вообще игнорируется.

## Требования для сборки
Windows 10/11

[.NET SDK 8.x](https://dotnet.microsoft.com/ru-ru/download/dotnet/8.0)

## Сборка
```bash
dotnet publish -c Release -p:PublishProfile=Portable
```
