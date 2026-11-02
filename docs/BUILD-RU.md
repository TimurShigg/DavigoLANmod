# Сборка

Требуется Windows, .NET 6 SDK, установленная платная DAVIGO `0.5.2A-1493` и уже инициализированный BepInEx IL2CPP.

После первого запуска игры с BepInEx в её папке должны существовать `BepInEx/core` и `BepInEx/interop`. Затем выполните из корня репозитория:

```powershell
dotnet build .\DavigoTrueLan.csproj -c Release -p:DavigoDir="C:\Program Files (x86)\Steam\steamapps\common\Davigo"
```

Результат появится в `bin/Release/net6.0/DavigoTrueLan.dll`.

Для ручной установки DLL копируется в:

```text
<папка игры>\BepInEx\plugins\DavigoTrueLan\DavigoTrueLan.dll
```

Готовые релизные архивы уже включают BepInEx, конфигурацию и установщики. Библиотеки игры и сгенерированные interop-сборки в Git не добавляются.

