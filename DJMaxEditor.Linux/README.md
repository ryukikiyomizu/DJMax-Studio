# DJMAX Studio Linux

A native Avalonia desktop frontend for Linux that reuses the repository's shared chart model and codecs. This is an initial portable workspace, not a feature-complete port of the Windows WPF/WinForms editor.

## Supported in this frontend

- Open PT, classic BMS family (`.bms`, `.bme`, `.bml`, `.pms`), bmson, and TECHMANIA `.tech` charts.
- Save or convert charts to PT, BMS family, bmson, and `.tech`.
- Create a basic chart, click a lane to place/remove notes, and review a compact timeline overview.
- Open positively identified encrypted Technika PT files using the existing offline codec; saves are written as decrypted PT.

Other legacy containers and the Windows editor's advanced tools are not included yet.

## Run from source

Requires the .NET 8 SDK and a Linux desktop session with its normal GTK/Skia native dependencies:

```sh
dotnet run --project DJMaxEditor.Linux/DJMaxEditor.Linux.csproj
```

Open a chart from the UI or pass one as the first argument:

```sh
dotnet run --project DJMaxEditor.Linux/DJMaxEditor.Linux.csproj -- ./chart.bms
```

## Publish a self-contained Linux x64 executable

```sh
dotnet publish DJMaxEditor.Linux/DJMaxEditor.Linux.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish/linux-x64
```

Run `publish/linux-x64/DJMaxEditor.Linux`. The GitHub Actions `DJMaxStudio-linux-x64` artifact packages this executable in a tar.gz archive so its executable permission is retained.
