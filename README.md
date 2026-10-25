# Whiteboard Simulator

Quadro branco digital para Windows 11 (C#, WPF, .NET 8, MVVM simples).

- Moldura de metal ou madeira; superfície de quadro branco, lousa preta, lousa verde ou veludo verde; tema claro/escuro.
- Ferramentas: selecionar, caneta, borracha (apaga só onde toca), texto formatado por trecho, linha, quadrado, círculo, régua, post-it, alfinete, linha de ligação e imagem.
- Abas de quadros, salvar/abrir (`.wboard`), recentes e favoritos, salvamento automático com recuperação, exportar PNG/PDF, imprimir e pacote compartilhável (`.wbpack`).
- Desfazer/refazer, zoom e deslocamento, ícone na bandeja do Windows.

## Executar

```
dotnet run
```

## Instalador (recomendado para distribuir)

O instalador (`installer/`, feito com [Inno Setup 6](https://jrsoftware.org/isinfo.php)) instala o app e, **antes dele**,
o .NET 8 Desktop Runtime caso o computador ainda não tenha (baixa da Microsoft na hora, em silêncio).
Fica com ~4 MB porque o app vai sem o .NET dentro.

```
winget install JRSoftware.InnoSetup
powershell -File installer\build-installer.ps1 -Version 0.1.0
```

O resultado fica em `installer\output\WhiteboardSimulator-Setup-<versão>.exe`. Também aceita instalação silenciosa
(`/VERYSILENT`) e, sem administrador, `/CURRENTUSER` (nesse modo não instala o runtime).

## Gerar o executável único com o .NET embutido (~156 MB, sem instalação)

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Estrutura

- `Models/` — elementos do quadro (posição, tamanho e rotação), documento e aparência.
- `ViewModels/` — `MainViewModel`, `BoardViewModel` (um por aba), estado das ferramentas.
- `Controls/` — `BoardCanvas` (mouse e ferramentas) e `ElementView` (desenho de cada elemento).
- `Helpers/` — arquivos, recuperação, exportação, desfazer/refazer.
- `SelfTest.cs` — autoteste das ferramentas (só em Debug): `WhiteboardSimulator.exe --selftest <pasta>`.
