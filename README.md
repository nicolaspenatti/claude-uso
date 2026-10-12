# Claude Uso

Ícone na bandeja do Windows que mostra o uso do seu plano do Claude nas janelas de **5 horas** e **7 dias**, com os mesmos números do app do Claude. Minimalista, sem dependências e com um único arquivo de código.

- Porcentagem **oficial** do plano, com o horário em que cada janela reinicia
- Ícone com a porcentagem da janela de 5h (laranja, âmbar a partir de 60% e vermelho a partir de 85%)
- Painel compacto ao clicar, com as duas janelas
- **Modo por sessão**: lista o consumo de cada CLI/projeto aberto, com um ponto verde na sessão ativa
- Sem login do Claude Code ou sem rede, cai para uma estimativa local e avisa com "estimado"
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
| Primeira linha (cinza) | Mostra se os dados são oficiais ("Uso oficial · atualizado 10:46") ou estimados, e o motivo |
| Atualizar agora | Consulta e recalcula na hora |
| Limites da estimativa… | Define os limites de 5h e de 7 dias usados só na estimativa, em milhões de tokens |
| Verificação periódica | 1, 2 ou 5 min (a atualização principal é automática, por monitoramento de arquivos) |
| Contabilizar por sessão | Mostra o consumo por sessão/projeto |
| Iniciar com o Windows | Abre junto com o sistema |

As configurações ficam em `HKCU\Software\UsoClaude`.

## Como funciona

### Uso oficial

O app lê o login que o Claude Code guarda em `%USERPROFILE%\.claude\.credentials.json` e consulta `https://api.anthropic.com/api/oauth/usage`, o mesmo endpoint que o Claude Code usa para mostrar os limites do plano. A resposta traz a porcentagem e o horário de reinício de cada janela, já somando o uso no Claude Code, no app e no site do Claude.

- As consultas automáticas respeitam um intervalo mínimo de 1 minuto: acontecem quando o Claude Code escreve algo e a cada verificação periódica. Abrir o painel ou usar *Atualizar agora* força uma consulta nova. Se a API pedir para esperar (erro 429), o app respeita o tempo indicado.
- O login é só **lido**. O app não o grava, não o renova e só o envia para a API da Anthropic. Nada mais sai do computador.
- Quem renova o login é o Claude Code. Se ele estiver expirado (por exemplo, depois de muitas horas sem uso), o app mostra a estimativa até o Claude Code renová-lo.
- O endpoint não é documentado oficialmente e pode mudar. Se a resposta vier em outro formato, o app volta para a estimativa em vez de mostrar números errados.

### Estimativa local

Usada quando o uso oficial não está disponível. O app lê os transcripts do Claude Code em `%USERPROFILE%\.claude\projects` e soma os tokens de cada resposta (entrada + saída + criação de cache), sem contar mensagens repetidas. Um `FileSystemWatcher` observa essa pasta e recalcula assim que o Claude Code escreve algo; uma verificação periódica mais lenta cobre o envelhecimento das janelas.

A porcentagem sai da divisão por limites que **não vêm da Anthropic**: os padrões são 5 milhões de tokens em 5h e 50 milhões em 7 dias, e você ajusta em *Limites da estimativa…*. Por isso ela é aproximada, e o painel marca "estimado" enquanto está nesse modo.

### Limitações

- A estimativa conta só o uso do Claude Code **neste computador**. O uso no app ou no site do Claude não entra nela (no uso oficial, entra).
- A API não divide o uso por sessão. No modo por sessão, o app reparte a porcentagem oficial entre as sessões na proporção dos tokens locais de cada uma. É uma aproximação: uso feito fora do Claude Code (no site, por exemplo) acaba atribuído às sessões listadas.

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
