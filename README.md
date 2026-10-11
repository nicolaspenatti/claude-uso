# Claude Uso

Ícone na bandeja do Windows que mostra o uso do **Claude Code** nas últimas 5 horas e nos últimos 7 dias. Minimalista, sem dependências e com um único arquivo de código.

- Ícone com a porcentagem das últimas 5h (laranja, âmbar a partir de 60% e vermelho a partir de 85%)
- Painel compacto ao clicar, com as duas janelas de tempo
- **Modo por sessão**: lista o consumo de cada CLI/projeto aberto, com um ponto verde na sessão ativa
- Todas as configurações pelo menu do ícone, sem editar arquivos

## Como usar

1. Baixe o `Claude-Uso.zip` na aba **Releases** (ou compile, veja abaixo).
2. Extraia a pasta e dê dois cliques em `claude-uso.exe`.
3. Procure o ícone com um número perto do relógio. Se não aparecer, clique na seta `^` e arraste o ícone para fora.

> O Windows pode mostrar o aviso do SmartScreen porque o app não é assinado.
> Clique em **Mais informações → Executar assim mesmo**.

## Menu (clique direito no ícone)

| Opção | O que faz |
|---|---|
| Atualizar agora | Recalcula na hora |
| Limites… | Define os limites de 5h e de 7 dias, em milhões de tokens |
| Atualizar a cada | 15 s, 30 s, 1 min ou 5 min |
| Contabilizar por sessão | Mostra o consumo por sessão/projeto |
| Iniciar com o Windows | Abre junto com o sistema |

As configurações ficam em `HKCU\Software\UsoClaude`.

## Limites

Os valores padrão (5 milhões em 5h e 50 milhões em 7 dias) são **estimativas**. Ajuste em *Limites…* conforme o seu plano.

## Como funciona

O app lê os transcripts locais do Claude Code em `%USERPROFILE%\.claude\projects` e soma os tokens de cada resposta (entrada + saída + criação de cache), sem contar mensagens repetidas. Nada é enviado para a internet.

Limitações:

- Conta só o uso do Claude Code **neste computador**. O uso no app ou no site do Claude não entra.
- Os limites não vêm da Anthropic. Por isso a porcentagem é uma estimativa e não o valor oficial do plano.

## Compilar

Precisa só do Windows 10/11 (o compilador do .NET Framework já vem nele):

```powershell
.\build.ps1
```

Ou direto:

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /out:claude-uso.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll UsoClaude.cs
```

Tecnologias: C# com Windows Forms (.NET Framework 4.8) e desenho via GDI+.
