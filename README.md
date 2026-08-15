# Construction Animation

Protótipo inicial de um code mod para Cities: Skylines II que pretende substituir a apresentação visual instantânea da construção de edifícios por uma animação procedural por fases.

## Estado atual: V0.1

Esta versão é deliberadamente conservadora. Não altera entidades vanilla nem grava dados no save. Apenas cria um `GameSystemBase` que procura entidades com `Game.Objects.UnderConstruction` e escreve no log sempre que o número de entidades em construção muda.

## Instalação do projeto em C:\Dev

1. Extrair este pacote para qualquer pasta temporária.
2. Executar `setup.ps1` com PowerShell.
3. O script valida as DLLs do jogo e cria/atualiza `C:\Dev\ConstructionAnimation`.
4. Abrir `C:\Dev\ConstructionAnimation\ConstructionAnimation.csproj` no Visual Studio 2022.

O projeto está configurado para esta instalação do jogo:

`C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Managed`

## Primeiro objetivo

Quando o mod estiver carregado e uma construção começar/terminar, queremos encontrar mensagens semelhantes a:

```text
[ConstructionAnimation] [INFO] Construction Animation v0.1 loaded.
[ConstructionAnimation] [INFO] ConstructionDetectionSystem created.
[ConstructionAnimation] [INFO] UnderConstruction entities: 1
[ConstructionAnimation] [INFO] UnderConstruction entities: 2
```

## Roadmap técnico

- V0.1: detetar `UnderConstruction`.
- V0.2: inspecionar o conteúdo do componente e identificar o progresso real.
- V0.3: associar progresso, prefab, posição e dimensões do edifício.
- V0.4: protótipo visual sem alterar a entidade de simulação.
- V0.5: crescimento vertical procedural.
- V0.6: fases de fundações, estrutura, fachada e acabamento.
- V0.7: gruas, andaimes e props procedurais.

## Nota sobre o toolchain oficial

O `.csproj` usa diretamente as assemblies da pasta `Managed` para tornar as referências explícitas. Se a variável de utilizador `CSII_TOOLPATH` estiver configurada e contiver `Mod.props`/`Mod.targets`, o projeto também tenta importar a integração oficial de build/deploy do jogo.
