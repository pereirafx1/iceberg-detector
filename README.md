# Iceberg Detector — ATAS Indicator

## Pré-requisitos
- Visual Studio 2022 Community (workload: .NET desktop development)
- .NET 8 SDK
- ATAS Platform instalada em: C:\Program Files (x86)\ATAS Platform\
- Data provider: Rithmic (obrigatório para dados MBO)

## Compilar
1. Abrir IcebergDetector.sln no Visual Studio 2022
2. Confirmar que os HintPath no .csproj apontam para a pasta da ATAS
3. Build > Build Solution (Ctrl+Shift+B)
4. DLL gerada em: bin\Debug\net8.0\IcebergDetector.dll

## Instalar
1. Copiar IcebergDetector.dll para: %APPDATA%\ATAS\Indicators\
2. Abrir ATAS Platform
3. No painel de indicadores, clicar em "Reload Indicators"
4. Procurar "Iceberg Detector" na categoria "Order Flow"
5. Adicionar ao gráfico (funciona em Footprint e Candlestick charts)

## Notas
- Requer feed Rithmic com dados MBO activos
- Sem MBO, o indicador exibe aviso nos logs mas não causa erros
- Compatível com ATAS Classic (Windows) e ATAS X (cross-platform)
