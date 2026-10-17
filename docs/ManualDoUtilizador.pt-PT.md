# PDF para DICOM — Manual do Utilizador

**Versão 3.0 · Para profissionais hospitalares**

Esta aplicação recebe um documento PDF produzido por outro sistema, converte-o num objeto
DICOM de PDF Encapsulado e envia-o para o PACS, de forma a que o documento passe a fazer
parte do registo imagiológico do doente.

Não precisa de conhecer DICOM para a utilizar. Se sabe preencher um formulário, sabe
utilizar esta aplicação.

---

## Índice

1. [Antes de começar](#1-antes-de-começar)
2. [A janela principal](#2-a-janela-principal)
3. [Passo 1 — Escolher o documento ou a imagem](#3-passo-1--escolher-o-documento-ou-a-imagem)
   - [Dados do doente lidos do nome do ficheiro](#dados-do-doente-lidos-do-nome-do-ficheiro)
4. [Passo 2 — Introduzir os dados do doente](#4-passo-2--introduzir-os-dados-do-doente)
   - [Obter o doente a partir da lista de trabalho](#obter-o-doente-a-partir-da-lista-de-trabalho)
   - [Escolher a lista de trabalho](#escolher-a-lista-de-trabalho)
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
│  PDF para DICOM              Registos  Definições  Acerca │ 🚪 Sair   │  ← cabeçalho
│  Hospital de Exemplo                                                 │
├───────────────────────────────────────┬──────────────────────────────┤
│  1. Selecionar o documento PDF        │  3. PACS de destino          │
│  ┌─────────────────────────────────┐  │  ┌────────────────────────┐  │
│  │  Arraste um PDF para aqui       │  │  │ PACS Principal         │  │
│  └─────────────────────────────────┘  │  │ PACS@192.168.10.20:104 │  │
│                                       │  └────────────────────────┘  │
│  2. Informação do doente              │   [Testar ligação]           │
│  [Modelo ▾] [Da lista de trabalho...] │                              │
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

**Fechar a aplicação.** O ícone de porta no canto superior direito fecha a aplicação.
Utilize-o quando a janela tiver sido movida ou redimensionada de tal forma que o botão de
fechar habitual, no canto, não esteja visível. Pede sempre confirmação e, se estiver um
documento a ser gerado ou enviado, avisa antes de interromper. Nada do que já foi escrito
na pasta de saída é apagado, pelo que um documento interrompido durante o envio pode ser
enviado de novo.

**O ponto colorido na barra de estado indica em que fase se encontra:**

| Ponto | Significado |
|:---:|---|
| ⚪ Cinzento | Pronto. À espera de si. |
| 🔵 Azul | A trabalhar. A gerar ou a enviar. |
| 🟢 Verde | Concluído com sucesso. |
| 🔴 Vermelho | Ocorreu uma falha. Leia a mensagem. |

---

## 3. Passo 1 — Escolher o documento ou a imagem

**A partir da versão 2.0 pode converter imagens além de documentos PDF.** São aceites
JPEG, PNG, TIFF, BMP, GIF, WebP e TGA, o que cobre tudo o que um digitalizador, uma máquina
fotográfica ou um telemóvel costumam produzir.

Pode selecionar um ficheiro de três formas. Utilize a que lhe for mais cómoda.

**a) Arrastar e largar.** Arraste o ficheiro do Explorador do Windows ou do ambiente de
trabalho e largue-o em qualquer ponto da janela. A área de largada fica realçada quando o
ficheiro vai ser aceite.

**b) Procurar.** Clique em **Procurar...** e escolha o ficheiro.

**c) Automaticamente.** Se o departamento de informática tiver ativado a monitorização de
pastas, um PDF guardado na pasta vigiada é recolhido sozinho. A barra de estado indica
*Recolhido da pasta monitorizada*.

Depois de selecionado, verá o nome, o tamanho, a data e o número de páginas, e uma linha a
descrever o que o ficheiro foi reconhecido como sendo. **Confirme estes dados antes de
continuar.** O número de páginas é a forma mais rápida de confirmar que tem o relatório
completo e não apenas uma página.

### Dados do doente lidos do nome do ficheiro

Se o departamento de informática também tiver ativado esta opção, os campos do doente são
preenchidos sozinhos a partir do nome do ficheiro recolhido. Um documento produzido por
outro sistema costuma já trazer os identificadores no nome, pelo que não há nada a
reescrever.

Um ficheiro chamado

```
9999999999_JohnDoe_TEST123451.pdf
```

preenche o Nº de processo do doente *9999999999*, o nome *JohnDoe* e o Número de acesso
*TEST123451*. A
barra de estado indica *Os dados do doente foram preenchidos a partir do nome do ficheiro.
Confirme-os antes de enviar.*

Há três pontos a ter em conta.

- **Confirme o que foi preenchido.** Qualquer pessoa pode mudar o nome de um ficheiro, pelo
  que o nome não é prova de identidade. Nada é enviado até premir **Converter e Enviar** e
  confirmar, exatamente como antes. A confirmação existe precisamente para este momento.
- **Um número de processo curto recupera os zeros à esquerda.** Muitos sistemas escrevem
  `77` onde os seus registos têm `0000000077`. Para o arquivo, esses são dois doentes
  diferentes, pelo que o número é completado até à largura utilizada no seu serviço, dez
  dígitos salvo indicação em contrário do departamento de informática. Um número de
  processo com letras fica exatamente como estava.
- **Campos vazios são normais.** A maioria dos esquemas de nomes não inclui a data de
  nascimento, pelo que esta fica vazia e o sexo é registado como *Outro* em vez de ser
  adivinhado. Quando a data de nascimento é genuinamente desconhecida, deixá-la vazia é
  correto; inventá-la não é.
- **Um nome não reconhecido não altera nada.** Se o nome do ficheiro não seguir o formato
  acordado, o formulário fica simplesmente à sua espera para ser preenchido à mão, que é
  como a aplicação se comportava antes desta funcionalidade existir.

**A data do estudo vem do ficheiro.** A data e a hora de criação do ficheiro são registadas
como data e hora do estudo, porque é normalmente nesse momento que o outro sistema emitiu o
documento. A data em que o próprio objeto DICOM foi criado é registada em separado, pelo que
ambas ficam preservadas.

**O ficheiro é arrumado no fim.** Depois de um documento ser tratado, o original é movido
para uma pasta **Imported**, ao lado da pasta vigiada, para que não seja recolhido outra
vez. Se algo tiver corrido mal, vai para uma pasta **Failed**, onde pode ser examinado.
Nunca nada é apagado nem substituído; se já existir um ficheiro com o mesmo nome, é
acrescentado um número ao novo.

Se os campos forem preenchidos de forma errada, ou não forem preenchidos, o departamento de
informática pode alterar o formato dos nomes em **Definições**, na página **Nomes de
ficheiros**, onde existe também uma caixa que experimenta um nome de ficheiro e mostra
exatamente o que seria lido a partir dele. A secção 11 indica o que lhes dizer.

### O que acontece a uma imagem

Uma linha por baixo dos detalhes do ficheiro indica exatamente o que vai ser produzido,
por exemplo *JPEG, 2480 x 3508, tons de cinzento. Será convertida numa imagem DICOM.*

- **Uma imagem única** dá origem a uma imagem DICOM.
- **Uma digitalização com várias páginas**, normalmente um TIFF de várias páginas, dá
  origem a uma imagem DICOM por página. Ficam todas juntas como um só item no arquivo e um
  visualizador permite percorrê-las.

Vale a pena saber duas coisas:

- **Uma digitalização a preto e branco continua em tons de cinzento.** A aplicação observa
  os pixels em vez de confiar no ficheiro, pelo que um digitalizador que guarde uma página
  a preto e branco como ficheiro a cores produz na mesma uma imagem DICOM em tons de
  cinzento. Ocupa cerca de um terço e não se perde nada.
- **A imagem DICOM é maior do que o ficheiro de origem**, muitas vezes bastante maior,
  porque o DICOM guarda a imagem sem compressão. Uma fotografia de quatro megabytes pode
  dar origem a uma imagem DICOM de trinta megabytes. Isto é normal e é o que os arquivos
  esperam. Se uma transferência parecer lenta, a razão é esta.

**A regra de um ficheiro de cada vez também se aplica às imagens:** largue um ficheiro,
converta-o e só depois largue o seguinte.

> **Apenas um ficheiro de cada vez.** Se largar vários ficheiros não acontece nada. Isto é
> intencional: adivinhar qual pretendia é exatamente a forma como um documento acaba
> arquivado no doente errado.

### Se o documento for recusado

| Mensagem | Significado | O que fazer |
|---|---|---|
| *já não existe* | O ficheiro foi movido ou eliminado. | Volte a localizá-lo. |
| *está vazio* | O ficheiro não tem conteúdo. | O sistema que o produziu não terminou. Produza-o de novo. |
| *não começa por um cabeçalho PDF* | Não é realmente um PDF, apesar do nome. | Peça um PDF verdadeiro ao sistema de origem. |
| *não é um PDF nem uma imagem num formato suportado* | O ficheiro é outra coisa, por exemplo um documento Word ou uma folha de cálculo. | Exporte-o primeiro para PDF e converta esse ficheiro. |
| *está danificada e não pôde ser lida* | O ficheiro de imagem está incompleto ou corrompido. | Digitalize ou exporte novamente. |
| *está encriptado* | O PDF está protegido por palavra-passe. | Guarde uma cópia sem proteção. Ninguém conseguiria abrir um documento encriptado a partir do PACS. |
| *pode estar danificado ou ainda a ser escrito* | O ficheiro está incompleto. | Aguarde alguns segundos e tente novamente. |

---

## 4. Passo 2 — Introduzir os dados do doente

**Este é o passo mais importante de todo o processo.** Tudo o resto é mecânico; é aqui que
um erro chega ao registo do doente.

### Obter o doente a partir da lista de trabalho

**Se o seu serviço tiver uma lista de trabalho de modalidade, utilize-a.** Escrever um
número de processo à mão é a forma mais provável de um documento acabar no registo do
doente errado, e a lista de trabalho elimina esse passo por completo.

Prima **Da lista de trabalho...**, por cima dos campos do doente.

> **Se não conseguir ver esse botão**, ainda não foi ativada nenhuma lista de trabalho. O
> botão aparece assim que o departamento de informática tiver configurado uma e assinalado
> **Ativada** na página Lista de trabalho, em Definições; uma lista configurada mas não
> ativada não mostra botão nenhum. Peça-lhes que executem `pdftodicom config`, que indica
> claramente se o botão deve estar visível e enumera os servidores conhecidos. Até lá,
> preencha o formulário como habitualmente.

Abre-se uma janela com os procedimentos agendados para hoje:

```
┌────────────────────────────────────────────────────────────────────────────┐
│  Lista de Trabalho de Modalidade                                           │
│  Escolha o procedimento agendado a que este documento pertence.            │
│                                                                            │
│  Lista de trabalho a consultar                                             │
│  [ Radiologia             ▾ ]                                              │
│                                                                            │
│  ID do doente contém      Apelido contém                                   │
│  [________________]       [________________]  [Procurar] [Mostrar tudo]    │
│                                                                            │
│  ID do doente │ Nome                 │ Data nasc. │ Nº acesso │ Procedim.  │
│  P0000002     │ GONÇALVES, JOÃO MIG. │ 1970-09-03 │ ACC...002 │ Relat...   │
│  P0000006     │ MARTINS, BEATRIZ     │ 2001-04-18 │ ACC...006 │ Radio...   │
│                                                                            │
│  Será utilizado                                                            │
│  GONÇALVES, JOÃO MIGUEL                                                    │
│                                   [Cancelar]  [Utilizar este doente]       │
└────────────────────────────────────────────────────────────────────────────┘
```

- **Deixe ambas as caixas vazias** para ver a lista completa. Escreva parte de um apelido
  ou de um número de processo e prima **Procurar** para a restringir.
- Faça duplo clique numa linha, ou selecione-a e prima **Utilizar este doente**.
- Os campos do doente são preenchidos automaticamente, e o número de acesso passa a vir
  do pedido e não dos papéis que estão em cima da secretária.

**Procurar um doente pelo número de processo.** Escreva o número de processo em **Nº de
processo contém** e prima **Procurar**. Se houver exatamente um procedimento agendado que
corresponda, é selecionado automaticamente e basta premir **Utilizar este doente**. Se o
doente tiver vários procedimentos agendados, aparecem todos e escolhe o correto.

### Escolher a lista de trabalho

**Se o hospital tiver mais do que uma lista de trabalho**, por exemplo uma para radiologia
e outra para cardiologia, aparece uma caixa **Lista de trabalho a consultar** no topo da
janela. Escolha o serviço cuja lista pretende; a pesquisa é repetida imediatamente.

A escolha é memorizada, pelo que se utilizar sempre o mesmo serviço não terá de a repetir.

Existe também a opção **Todas as listas de trabalho**. Utilize-a quando tiver um documento
em mãos e não souber que serviço agendou o exame: consulta todas as listas ao mesmo tempo e
uma coluna adicional, **Lista de trabalho**, indica de que serviço vem cada linha. Essa
coluna é importante quando o mesmo doente está agendado em dois serviços, porque é assim
que distingue a qual dos dois o documento pertence.

Se a lista de trabalho de um serviço estiver indisponível, as restantes continuam a
responder. A linha por baixo da lista identifica a que falhou, para que saiba que se trata
do servidor desse serviço e não de todos.

**Confirme a linha "Será utilizado" antes de premir o botão.** Mostra exatamente o que vai
ficar no registo do doente. Normalmente coincide com a linha acima. Ocasionalmente é mais
curta, pelo motivo explicado a seguir.

**Porque é que um nome muito longo é por vezes abreviado.** O DICOM permite 64 carateres
para um nome. Alguns nomes portugueses são mais longos do que isso, e a norma não oferece
forma de os enviar por inteiro. O apelido é mantido completo, por ser aquilo por que um
arquivo indexa e pesquisa, e o nome próprio é cortado no fim de uma palavra, para que
continue a ler-se como um nome. *ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES, JOSÉ ANTÓNIO
FRANCISCO XAVIER* é enviado como *ALBUQUERQUE MONTENEGRO DOS SANTOS RODRIGUES, JOSÉ
ANTÓNIO*. O nome completo permanece na lista de trabalho, a abreviatura fica registada no
registo de atividade, e nada o interrompe.

**Os nomes do meio não são transportados.** O serviço não os utiliza, e deixá-los de fora
significa que não consomem os carateres de que o primeiro e o último nome precisam.

**A lista de trabalho é apenas de leitura.** Nunca lhe é enviado nada de volta e nenhum
dado de doente sai da sua estação de trabalho quando a abre. Se escolher a linha errada,
basta abrir de novo a janela e escolher outra: o formulário é substituído por inteiro,
nunca misturado.

### Se não vier nada

| O que vê | O que normalmente significa |
|---|---|
| *A lista de trabalho não devolveu procedimentos agendados para esta consulta* | O doente não está agendado para hoje. Peça ao departamento de informática para alargar o intervalo de datas, ou preencha o formulário à mão. |
| *não foi possível contactar* | O servidor da lista de trabalho está indisponível, ou o endereço está errado. Preencha o formulário à mão e avise a informática. |
| *rejeitou a ligação* | Esta estação de trabalho não está registada no servidor da lista de trabalho. A informática tem de o fazer uma vez. |
| *Existiam mais resultados do que os apresentados* | Restrinja a procura com um apelido ou um número de processo. |

Pode sempre escrever os dados. A lista de trabalho é uma comodidade, nunca uma obrigação.

### Campos obrigatórios

Preencha-os quando não estiver a utilizar a lista de trabalho.

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
| **Destinos** | Os sistemas PACS de destino, com **Testar ligação** por destino. Protegido pela mesma palavra-passe da página Lista de trabalho: um endereço mal escrito aqui enviaria um relatório para o arquivo errado. |
| **DICOM** | Conjunto de carateres, geração de identificadores, modalidade, descrições por omissão. Não altere sem indicação do administrador do PACS. |
| **Lista de trabalho** | Os servidores de listas de trabalho de modalidade, tantos quantos o hospital tiver. Protegido por uma palavra-passe na posse de quem instalou a aplicação; apontar um deles para o servidor errado colocaria à sua frente os doentes errados. |
| **Pastas** | Onde são guardados os ficheiros e os registos, e a monitorização de pastas. |
| **Nomes de ficheiros** | O formato utilizado para ler os dados do doente a partir do nome do ficheiro, e uma caixa que experimenta um nome e mostra o que seria lido. Ver a secção 3. |
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

Se utilizar a lista de trabalho, o mesmo registo tem de existir também no **servidor da
lista de trabalho**. É um sistema distinto do PACS, e estar registado num não o regista no
outro.

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

**O botão "Da lista de trabalho..." não aparece.**
Não foi configurada nenhuma lista de trabalho nesta estação. Pergunte ao departamento de
informática se o seu serviço tem uma. Até lá, preencha os campos do doente à mão.

**O nome na lista de trabalho é mais longo do que o nome enviado.**
O DICOM permite 64 carateres para um nome e alguns nomes são mais longos. O apelido é
mantido por inteiro e o nome próprio é abreviado, pelo que o registo continua a
identificar a pessoa certa. É o comportamento esperado e fica registado.

**Se o pedido for alterado, os dados mudam no formulário?**
Não. Os dados são copiados para o formulário no momento em que prime *Utilizar este
doente*. Nada é relido depois disso, e nunca é escrito nada de volta na lista de
trabalho.

---

*Para detalhe técnico, consulte `TechnicalDocumentation.md` e
`DicomConformanceStatement.md` na mesma pasta.*
