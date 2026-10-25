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

## Gerar o executável (arquivo único, sem precisar do .NET instalado)

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Estrutura

- `Models/` — elementos do quadro (posição, tamanho e rotação), documento e aparência.
- `ViewModels/` — `MainViewModel`, `BoardViewModel` (um por aba), estado das ferramentas.
- `Controls/` — `BoardCanvas` (mouse e ferramentas) e `ElementView` (desenho de cada elemento).
- `Helpers/` — arquivos, recuperação, exportação, desfazer/refazer.
- `SelfTest.cs` — autoteste das ferramentas (só em Debug): `WhiteboardSimulator.exe --selftest <pasta>`.
