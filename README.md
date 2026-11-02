# postech-execucao-service

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-execucao-service&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=postech4_postech-execucao-service) [![Cobertura](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-execucao-service&metric=coverage)](https://sonarcloud.io/summary/new_code?id=postech4_postech-execucao-service) [![Bugs](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-execucao-service&metric=bugs)](https://sonarcloud.io/summary/new_code?id=postech4_postech-execucao-service) [![Vulnerabilidades](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-execucao-service&metric=vulnerabilities)](https://sonarcloud.io/summary/new_code?id=postech4_postech-execucao-service) [![CI/CD](https://github.com/Gustavollier/postech-execucao-service/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/Gustavollier/postech-execucao-service/actions/workflows/ci-cd.yml)

Microsserviço de **Execução e Produção** do Tech Challenge Fase 4 (FIAP 13SOAT).

Dono da fila da oficina, do diagnóstico, do catálogo de peças e do estoque.

## O que este serviço faz

| Responsabilidade | Detalhe |
| --- | --- |
| Gerenciar a fila da OS | A ordem entra pelo `OrdemAberta` e a fila sai na ordem de atendimento: bancada, diagnóstico, pagamento |
| Diagnosticar | O mecânico lança os itens; as peças são reservadas e o `DiagnosticoConcluido` leva os itens ao Billing |
| Atualizar o status durante o reparo | `AguardandoDiagnostico` → `AguardandoLiberacao` → `EmExecucao` → `Finalizada` |
| Comunicar a finalização | `ExecucaoFinalizada` para o OS Service, que fecha a ordem |
| Estoque | Catálogo, entrada, reserva no diagnóstico, baixa na finalização |
| Compensar | `OrdemCancelada` tira a ordem da fila e devolve as peças |

Banco: **MongoDB** (`execucao-db`). Mensageria: **RabbitMQ**.

## O lugar na saga

A saga é **orquestrada** pelo OS Service ([ADR-0006](docs/ADR-0006-saga-orquestrada.md)).
O Execução participa de dois passos e de uma compensação:

```
consome                      publica
---------------------------  -------------------------------
OrdemAberta              ->  — (entra na fila)
(HTTP) diagnóstico       ->  DiagnosticoConcluido (com itens e cliente)
PagamentoConfirmado      ->  — (libera a bancada)
(HTTP) finalizar         ->  ExecucaoFinalizada
OrdemCancelada           ->  — (sai da fila e devolve as peças)
```

O `DiagnosticoConcluido` leva os itens e os dados do cliente porque é com
eles que o Billing monta o orçamento. Sem isso, o Billing teria que ler o
banco do Execução, que a fase proíbe, ou chamar este serviço por HTTP, e a
geração de orçamento passaria a depender dele estar no ar.

## Por que Mongo aqui

A ordem na oficina é lida e escrita **inteira**, com os itens dentro, e os
itens não têm vida fora dela. É o mesmo argumento do Billing
([ADR-0007](docs/ADR-0007-banco-por-servico.md)), e o mesmo desenho: o outbox
mora **dentro do documento**, então gravar a ordem e enfileirar o evento é uma
escrita só, atômica sem transação multi-documento. O M0 gratuito do Atlas não
oferece isso de forma confiável.

## O estoque

Esta é a parte que exigiu mais cuidado, porque a ordem e as peças são
documentos diferentes e não há transação entre eles.

**Reserva no diagnóstico, baixa na finalização.** Reservar no diagnóstico é o
que impede dois orçamentos de prometerem a mesma peça. O custo é que a reserva
vira mais um passo para a compensação desfazer.

**A reserva é por ordem, não um contador.** Cada peça guarda *quem* reservou:

```json
{ "Nome": "Filtro de óleo", "QuantidadeEmEstoque": 10,
  "Reservas": [ { "IdOrdem": 5001, "Quantidade": 2 } ] }
```

O laboratório da Fase 4 usava um contador `QuantidadeReservada` com piso em
zero, e isso tinha um defeito. Duas ordens reservam a mesma peça e o
`OrdemCancelada` da primeira chega duas vezes, coisa que a fila faz por
desenho. A segunda devolução consumia a reserva da outra ordem, e a peça
prometida a um cliente voltava a ficar disponível para outro. Com a reserva
chaveada pela ordem, reservar, devolver e baixar são idempotentes de verdade, e
há um teste que reproduz exatamente esse cenário.

**Cada operação é uma escrita condicional.** "Ler o saldo, mudar em memória,
gravar" deixa dois mecânicos reservarem a última peça. Aqui a condição vai
dentro do filtro:

```js
// reservar: ativa, sem reserva desta ordem, e com saldo
{ _id, Ativo: true, "Reservas.IdOrdem": { $ne: idOrdem },
  $expr: { $gte: [ { $subtract: ["$QuantidadeEmEstoque", { $sum: "$Reservas.Quantidade" }] }, qtd ] } }
```

Quem perde a corrida não casa com o filtro. Um teste dispara dez reservas
simultâneas contra cinco peças e confere que exatamente cinco passam.

**A ordem das escritas.** Primeiro o que é idempotente, por último a ordem:

| Caso de uso | Sequência | Se falhar no meio |
| --- | --- | --- |
| Diagnosticar | reserva as peças → grava a ordem | devolve o que **esta chamada** reservou; o estoque fica como estava |
| Finalizar | baixa as peças → devolve sobras → grava a ordem | repetir é seguro: sem reserva não há baixa |
| Cancelar | devolve todas as reservas da ordem → grava | repetir é seguro |

**Concorrência na ordem.** Dois diagnósticos da mesma ordem (um clique duplo
basta) passariam os dois pela checagem de etapa. A ordem tem um campo
`Versao`, e gravar exige que a versão no banco seja a lida. A perdedora recebe
409 e não desfaz a reserva que a vencedora fez.

**O preço da peça vem do catálogo.** No diagnóstico, item de peça leva o preço
do dia que está no catálogo, e não o que veio na requisição. O orçamento sai
daqui, e quem lança o item não escolhe o preço da peça.

## Rotas

| Método | Rota | O quê |
| --- | --- | --- |
| `GET` | `/api/v1/execucao/fila` | A fila na ordem de atendimento. `?etapa=` filtra |
| `GET` | `/api/v1/execucao/ordens/{idOrdem}` | A ordem na oficina, com itens |
| `POST` | `/api/v1/execucao/ordens/{idOrdem}/diagnostico` | Lança os itens, reserva peças, publica `DiagnosticoConcluido` |
| `POST` | `/api/v1/execucao/ordens/{idOrdem}/finalizar` | Baixa as peças, publica `ExecucaoFinalizada` |
| `GET` | `/api/v1/pecas` | Catálogo. `?incluirInativas=true` |
| `GET` | `/api/v1/pecas/{id}` | Uma peça, com saldo e reservas |
| `POST` | `/api/v1/pecas` | Cadastra |
| `POST` | `/api/v1/pecas/{id}/entrada` | Entrada de estoque |
| `GET` | `/health` · `/health/ready` | Liveness e readiness |

Swagger em `/swagger`. Exemplos em [`PosTech.Execucao.Api.http`](src/PosTech.Execucao.Api/PosTech.Execucao.Api.http).

As falhas de negócio têm status próprio, traduzidas num lugar só (`TradutorDeErros`):

| Situação | Status |
| --- | --- |
| Ordem não está na fila | 404 |
| Item inválido, diagnóstico sem itens | 400 |
| Peça do item não existe | 422 |
| Estoque insuficiente (corpo traz `idPeca`, `disponivel`, `pedido`) | 409 |
| Transição inválida (diagnosticar duas vezes, finalizar sem pagamento) | 409 |
| Outra gravação chegou primeiro | 409 |

## Rodar local

```bash
# Infraestrutura (RabbitMQ, Mongo, SQL Server)
docker compose -f docker-compose.yml up -d

export MongoConnectionString="mongodb://postech:senha@localhost:27017/?authSource=admin"
export RabbitMqConnectionString="amqp://postech:senha@localhost:5672/"
dotnet run --project src/PosTech.Execucao.Api
```

Os índices do `execucao-db` são criados na partida, de forma idempotente.

## Testes

```bash
dotnet test
```

**119 testes, 94% de cobertura de linha e 90% de ramo.** Os de integração
sobem MongoDB e RabbitMQ de verdade via Testcontainers, então é preciso ter
Docker.

| Grupo | O que prova |
| --- | --- |
| `FluxoDaExecucaoTests` | O serviço inteiro no ar: `OrdemAberta` entra pelo broker, diagnóstico por HTTP, `DiagnosticoConcluido` chega **na fila do Billing**, pagamento pela fila, `ExecucaoFinalizada` chega **na fila do orquestrador**, e a compensação devolve as peças |
| `PecaRepositorioTests` | A atomicidade do estoque: dez reservas simultâneas contra cinco peças, baixa e devolução que não tocam na reserva de outra ordem |
| `ServicoDaOficinaTests` | A ordem das escritas: diagnóstico que falha no meio não deixa peça presa, clique duplo reserva uma vez só |
| `ManipuladorDeEventosTests` | Entrega repetida e evento fora de hora não vão para a dead letter |
| `GestorDaExecucaoTests`, `EstoqueTests` | As regras puras |
| `ContratosTests`, `OutboxTests`, `BarramentoIntegracaoTests` | As bibliotecas compartilhadas, iguais nos quatro serviços |

A pipeline **reprova abaixo de 80% de cobertura**.

## Configuração

| Variável | O quê |
| --- | --- |
| `MongoConnectionString` | Conexão com o Atlas. Obrigatória |
| `RabbitMqConnectionString` | Conexão AMQP. Obrigatória |
| `MongoDatabase` | Nome do banco. Padrão `execucao-db` |

As duas primeiras são obrigatórias, e o serviço **se recusa a subir sem elas**.
No AKS, as duas vêm do Secret `execucao-service-secrets`.

O Billing e o Execução usam o mesmo cluster do Atlas, cada um com o **seu
banco e o seu usuário**. O usuário `execucao-svc` só tem `readWrite` em
`execucao-db`, então a regra de que nenhum serviço acessa o banco de outro vale
na credencial, e não só na disciplina do código.

## Documentação

- [ADR-0006: Saga orquestrada](docs/ADR-0006-saga-orquestrada.md)
- [ADR-0007: Um banco por serviço](docs/ADR-0007-banco-por-servico.md)
- [ADR-0008: CQRS só como separação de código](docs/ADR-0008-cqrs-sem-infraestrutura-nova.md)
