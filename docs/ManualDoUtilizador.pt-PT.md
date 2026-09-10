# PDF para DICOM — Manual do Utilizador

**Versão 1.1 · Para profissionais hospitalares**

Esta aplicação recebe um documento PDF produzido por outro sistema, converte-o num objeto
DICOM de PDF Encapsulado e envia-o para o PACS, de forma a que o documento passe a fazer
parte do registo imagiológico do doente.

Não precisa de conhecer DICOM para a utilizar. Se sabe preencher um formulário, sabe
utilizar esta aplicação.

---

## Índice

1. [Antes de começar](#1-antes-de-começar)
2. [A janela principal](#2-a-janela-principal)
3. [Passo 1 — Escolher o PDF](#3-passo-1--escolher-o-pdf)
4. [Passo 2 — Introduzir os dados do doente](#4-passo-2--introduzir-os-dados-do-doente)
5. [Passo 3 — Confirmar o destino](#5-passo-3--confirmar-o-destino)
6. [Enviar o documento](#6-enviar-o-documento)
7. [O que fazer quando algo corre mal](#7-o-que-fazer-quando-algo-corre-mal)
8. [Utilizar um leitor de códigos de barras](#8-utilizar-um-leitor-de-códigos-de-barras)
9. [Modelos](#9-modelos)
10. [Definições](#10-definições)
11. [Pedir ajuda ao departamento de informática](#11-pedir-ajuda-ao-departamento-de-informática)
12. [Perguntas frequentes](#12-perguntas-frequentes)

---

## 1. Antes de começar

**Precisa de três coisas:**

| | |
|---|---|
| **O PDF** | Já guardado num local que consiga encontrar. |
| **Os dados do doente** | Número de processo, nome, data de nascimento. |
| **O número de acesso** | Da requisição ou da worklist. |

**Deve também saber**, através do departamento de informática, que a estação de trabalho
foi registada no PACS. Enquanto isso não estiver feito, o envio falha independentemente do
que escrever. Consulte a [secção 11](#11-pedir-ajuda-ao-departamento-de-informática).

> **A aplicação funciona offline.** A rede só é utilizada no momento em que um documento é
> enviado para o PACS. Nunca envia nada para mais lado nenhum.

---

## 2. A janela principal

Tudo acontece numa só janela, que se lê de cima para baixo.

```
┌──────────────────────────────────────────────────────────────────────┐
│  PDF para DICOM                    Registos   Definições   Acerca    │  ← cabeçalho
│  Hospital de Exemplo                                                 │
├───────────────────────────────────────┬──────────────────────────────┤
│  1. Selecionar o documento PDF        │  3. PACS de destino          │
│  ┌─────────────────────────────────┐  │  ┌────────────────────────┐  │
│  │  Arraste um PDF para aqui       │  │  │ PACS Principal         │  │
│  └─────────────────────────────────┘  │  │ PACS@192.168.10.20:104 │  │
│                                       │  └────────────────────────┘  │
│  2. Informação do doente              │   [Testar ligação]           │
│  Nº processo *     Nº de acesso *     │                              │
│  [_____________]   [_____________]    │  Os resultados aparecem      │
│  Apelido *         Nome próprio       │  aqui após o envio.          │
│  [_____________]   [_____________]    │                              │
│  Data nasc. *      Sexo               │                              │
│  [__/__/____]      [Não especificado] │                              │
├───────────────────────────────────────┴──────────────────────────────┤
│  [Ver etiquetas] [Exportar registos]  [Apenas gerar] [Gerar e enviar]│  ← ações
├──────────────────────────────────────────────────────────────────────┤
│  ● Pronto                                                            │  ← barra estado
└──────────────────────────────────────────────────────────────────────┘
```

Os campos marcados com asterisco vermelho (**\***) são obrigatórios.

**O ponto colorido na barra de estado indica em que fase se encontra:**

| Ponto | Significado |
|:---:|---|
| ⚪ Cinzento | Pronto. À espera de si. |
| 🔵 Azul | A trabalhar. A gerar ou a enviar. |
| 🟢 Verde | Concluído com sucesso. |
| 🔴 Vermelho | Ocorreu uma falha. Leia a mensagem. |

---

## 3. Passo 1 — Escolher o PDF

Pode fazê-lo de três formas. Utilize a que lhe for mais cómoda.

**a) Arrastar e largar.** Arraste o PDF do Explorador do Windows ou do ambiente de trabalho
e largue-o em qualquer ponto da janela. A área de largada fica realçada quando o ficheiro
vai ser aceite.

**b) Procurar.** Clique em **Procurar...** e escolha o ficheiro.

**c) Automaticamente.** Se o departamento de informática tiver ativado a monitorização de
pastas, um PDF guardado na pasta vigiada é recolhido sozinho. A barra de estado indica
*Recolhido da pasta monitorizada*.

Depois de selecionado, verá o nome, o tamanho, a data de criação e o número de páginas.
**Confirme estes dados antes de continuar.** O número de páginas é a forma mais rápida de
confirmar que tem o relatório completo e não apenas uma página.

> **Apenas um ficheiro de cada vez.** Se largar vários ficheiros não acontece nada. Isto é
> intencional: adivinhar qual pretendia é exatamente a forma como um documento acaba
> arquivado no doente errado.

### Se o documento for recusado

| Mensagem | Significado | O que fazer |
|---|---|---|
| *já não existe* | O ficheiro foi movido ou eliminado. | Volte a localizá-lo. |
| *está vazio* | O ficheiro não tem conteúdo. | O sistema que o produziu não terminou. Produza-o de novo. |
| *não começa por um cabeçalho PDF* | Não é realmente um PDF, apesar do nome. | Peça um PDF verdadeiro ao sistema de origem. |
| *está encriptado* | O PDF está protegido por palavra-passe. | Guarde uma cópia sem proteção. Ninguém conseguiria abrir um documento encriptado a partir do PACS. |
| *pode estar danificado ou ainda a ser escrito* | O ficheiro está incompleto. | Aguarde alguns segundos e tente novamente. |

---

## 4. Passo 2 — Introduzir os dados do doente

**Este é o passo mais importante de todo o processo.** Tudo o resto é mecânico; é aqui que
um erro chega ao registo do doente.

### Campos obrigatórios

| Campo | Notas |
|---|---|
| **Nº de processo do doente** | Tem de corresponder exatamente ao do PACS, carácter a carácter. |
| **Apelido** | Nome de família. |
| **Data de nascimento** | Escolha no calendário ou escreva. |
| **Número de acesso** | Da requisição. É o que permite ao PACS ligar o documento ao pedido. Máximo de 16 caracteres. |

### Campos opcionais

**Nome próprio**, **Sexo**, **Médico requisitante**, **Título do documento**, **Descrição
do estudo**, **Descrição da série**. Tornam o documento mais fácil de encontrar mais tarde,
mas nada depende deles.

### Nomes com acentos

Escreva o nome exatamente como consta do registo do doente, acentos incluídos. *João*,
*Gonçalves*, *Aparício* são todos tratados corretamente e chegam ao PACS inalterados.

Se o departamento de informática tiver configurado o conjunto de carateres antigo (Latin 1),
um nome que utilize um alfabeto não latino é recusado com uma explicação, em vez de ser
corrompido silenciosamente.

### A aplicação não o deixa continuar se

- algum campo obrigatório estiver vazio,
- a data de nascimento for futura ou demasiado antiga,
- o número de acesso tiver mais de 16 caracteres,
- um nome contiver `^` ou `\`, carateres que o DICOM reserva para uso próprio.

O motivo aparece no painel da direita. Corrija e prossiga.

---

## 5. Passo 3 — Confirmar o destino

O painel da direita lista todos os PACS para onde o documento será enviado. A maioria dos
serviços tem apenas um.

**Testar ligação** envia uma mensagem inofensiva do tipo "está aí?" (um C-ECHO DICOM) a
cada destino e indica se houve resposta. **Não envia quaisquer dados do doente.** Utilize-a
sempre que tiver dúvidas.

Se o painel indicar *Não existe nenhum destino configurado*, a aplicação ainda não foi
configurada. Contacte o departamento de informática.

---

## 6. Enviar o documento

Clique em **Gerar e enviar**.

### A janela de confirmação

A menos que tenha sido desativada, aparece um resumo com o número de processo, nome, data
de nascimento, número de acesso, o ficheiro e o destino.

> **Leia-o.** É o último momento em que um erro pode ser detetado sem custo. Depois de o
> documento estar no PACS, removê-lo exige um administrador do PACS.

Clique em **OK** para prosseguir, ou em **Cancelar** para voltar atrás.

### O que acontece a seguir

A barra de estado percorre as fases:

1. *A verificar a informação introduzida*
2. *A gerar o objeto DICOM*
3. *A verificar … com C-ECHO*
4. *A enviar para …*
5. *Concluído*

Para um relatório normal, isto demora um ou dois segundos.

### Quando corre bem

O ponto fica verde e o painel de resultados mostra **OK** junto a cada destino. O documento
está agora no registo imagiológico do doente. Procure-o no PACS pelo número de processo e
pelo número de acesso que introduziu.

### Apenas gerar

**Apenas gerar** cria o ficheiro DICOM e guarda-o no disco **sem o enviar**. É útil quando
a rede está indisponível, ou quando o departamento de informática lhe pede um ficheiro de
exemplo. A localização é indicada no painel de resultados.

---

## 7. O que fazer quando algo corre mal

Nada se perde quando um envio falha. **Os seus dados permanecem no formulário** e o ficheiro
gerado é mantido no disco, pelo que clicar em **Tentar novamente** volta a enviá-lo sem que
tenha de escrever tudo outra vez.

| Mensagem | Causa provável | O que fazer |
|---|---|---|
| *A ligação foi recusada* | O PACS não está a atender, ou a porta está errada. | Clique em **Testar ligação**. Se também falhar, contacte a informática. |
| *não foi possível resolver o nome* | O endereço do PACS está errado ou o DNS está em baixo. | Contacte a informática. |
| *excedeu o tempo limite* | Uma firewall está a bloquear, ou a rede está em baixo. | Verifique se outras aplicações de rede funcionam e contacte a informática. |
| *rejeitou a associação* | O PACS não reconhece esta estação de trabalho. | O AE Title da estação precisa de ser registado no PACS. |
| *abortou a associação* | O PACS pode não aceitar documentos PDF, ou está ocupado. | Tente novamente. Se persistir, contacte a informática. |
| *rejeitou o documento* | O PACS recebeu-o mas recusou guardá-lo. | Anote o código de estado apresentado e envie-o à informática. |
| *não passou na validação* | Um problema interno produziu um objeto defeituoso. | Nada foi enviado. Exporte os registos e contacte a informática. |

Quando existem vários destinos, cada um é reportado separadamente. A falha de um não impede
os restantes.

---

## 8. Utilizar um leitor de códigos de barras

A maioria dos leitores hospitalares comporta-se como um teclado e funciona sem configuração.

1. Clique no campo **Nº de processo do doente** (já tem o cursor quando a aplicação abre).
2. Leia a pulseira ou a requisição.
3. O valor aparece. Se o leitor enviar um Enter, o cursor avança.

Leia para o campo **Número de acesso** da mesma forma.

Um Enter final enviado pelo leitor é tratado automaticamente. Se uma leitura produzir um
valor que a aplicação recuse, com uma mensagem que menciona o código de barras, a leitura
foi incorreta: limpe o campo e leia novamente.

---

## 9. Modelos

Se converte repetidamente o mesmo tipo de documento, guarde as partes que se repetem.

**Para guardar:** preencha o médico requisitante, a descrição do estudo, a descrição da
série e o título do documento, e clique em **Guardar como modelo...**.

**Para utilizar:** escolha-o na lista no topo da secção do doente e clique em **Modelos**.

> **Os modelos nunca guardam a identidade do doente.** Nenhum número de processo, nome,
> data de nascimento ou número de acesso é guardado. Reutilizar esses valores é a forma
> mais provável de arquivar um documento na pessoa errada, por isso a aplicação não o
> permite.

---

## 10. Definições

Clique em **Definições** no cabeçalho. A maioria destas opções é configurada uma única vez
pelo departamento de informática.

| Separador | Conteúdo |
|---|---|
| **Instituição** | Nome do hospital e do serviço, e o AE Title desta estação. |
| **Destinos** | Os sistemas PACS de destino, com **Testar ligação** por destino. |
| **DICOM** | Conjunto de carateres, geração de identificadores, modalidade, descrições por omissão. Não altere sem indicação do administrador do PACS. |
| **Pastas** | Onde são guardados os ficheiros e os registos, e a monitorização de pastas. |
| **Aspeto** | Idioma, modo escuro e confirmação antes de enviar. |

Duas definições são relevantes para o utilizador:

- **Modo escuro** — mais confortável numa sala de relatório com pouca luz.
- **Idioma** — português (Portugal) e inglês. A alteração é imediata.

> **Mantenha ativada a opção "Pedir confirmação antes de enviar".** É a última verificação
> de que o documento corresponde ao doente.

---

## 11. Pedir ajuda ao departamento de informática

### Exporte primeiro os registos

Clique em **Exportar registos**, guarde o ficheiro zip e junte-o ao seu pedido. Contém o
registo de diagnóstico, a trilha de auditoria e a configuração. **Não contém o PDF nem
quaisquer dados de imagem.**

### O que o departamento de informática tem de ter feito

Para que o envio funcione, o administrador do PACS tem de ter registado:

1. o **AE Title** desta estação (visível em Definições → Instituição),
2. o **endereço IP** desta estação,
3. autorização para enviar a classe SOP **Encapsulated PDF Storage**.

O ponto 3 é o mais frequentemente esquecido. Um PACS pode estar perfeitamente acessível e
ainda assim recusar documentos PDF por essa classe SOP nunca ter sido ativada.

---

## 12. Perguntas frequentes

**Isto envia alguma coisa pela Internet?**
Não. A única ligação de rede que estabelece é para os endereços PACS configurados pelo
departamento de informática, dentro da rede do hospital.

**Onde ficam guardados os ficheiros?**
No seu próprio perfil do Windows, em `%APPDATA%\PdfToDicom`. Utilize **Abrir pasta de
saída** no painel de resultados para lá chegar diretamente.

**Posso enviar o mesmo documento duas vezes?**
Pode, e cada tentativa cria um objeto distinto, pelo que o PACS ficará com duas cópias. Se
já o enviou com sucesso, não repita a menos que seja essa a intenção.

**Enviei para o doente errado. E agora?**
Contacte imediatamente o administrador do PACS. A aplicação não consegue recuperar um
documento depois de o arquivo o ter aceite. Anote o SOP Instance UID do painel de
resultados, pois identifica o objeto exato a remover.

**O documento está no PACS mas no estudo errado.**
Normalmente significa que o número de acesso não correspondia ao pedido. Confirme o que
introduziu e peça ao administrador do PACS que faça a reconciliação.

**Porque é que o ficheiro é tão grande?**
A aplicação é um único programa autónomo: todo o ambiente de execução .NET está incluído.
É isso que permite instalá-la copiando um só ficheiro, sem necessidade de instalar
previamente qualquer outro componente.

**Preciso de direitos de administrador?**
Não. Nem para instalar nem para utilizar.

---

*Para detalhe técnico, consulte `TechnicalDocumentation.md` e
`DicomConformanceStatement.md` na mesma pasta.*
